using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PiholeReportServer.Configuration;
using PiholeReportServer.Models;

namespace PiholeReportServer.Services;

/// <summary>
/// An analysis agent: it writes a query, reads the result, and decides whether it has
/// enough to answer or needs to look further.
/// <para>
/// Bounded on purpose. A step budget and a wall-clock budget, because the inference
/// host generates ~10 tokens/sec and an unbounded loop would tie up a page for many
/// minutes. Every query it proposes goes through <see cref="SqlGuard"/> and runs under
/// the same read-only login as the console, so the agent has no capability the user
/// does not already have by hand.
/// </para>
/// <para>
/// Results are fed back as a compact sample plus exact aggregates, not as thousands of
/// rows. Sending raw rows to this model was measured at ~150 seconds for 100 rows and
/// produced a recital rather than analysis; a sample large enough to reason over, with
/// the arithmetic done in C#, is both faster and more accurate.
/// </para>
/// </summary>
public sealed class AiAgent
{
    /// <summary>Rows of the result shown back to the model per step.</summary>
    private const int SampleRows = 15;

    private readonly AiClient _ai;
    private readonly ReportRunner _runner;
    private readonly AiOptions _opt;
    private readonly ReportingOptions _reporting;
    private readonly ILogger<AiAgent> _log;

    public AiAgent(
        AiClient ai,
        ReportRunner runner,
        IOptions<AiOptions> opt,
        IOptions<ReportingOptions> reporting,
        ILogger<AiAgent> log)
    {
        _ai = ai;
        _runner = runner;
        _opt = opt.Value;
        _reporting = reporting.Value;
        _log = log;
    }

    public bool Enabled => _ai.Enabled;

    public int MaxSteps => Math.Clamp(_opt.MaxAgentSteps, 1, 8);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private string SystemPrompt => $$"""
        You are a DNS traffic analyst with read-only SQL access to a Pi-hole warehouse.
        Answer the user's question by querying, reading the results, and reasoning.

        Reply with ONLY a JSON object, one of two shapes:

          {"action":"query","reasoning":"why this query","sql":"SELECT ..."}
          {"action":"answer","answer":"your conclusion, citing the actual figures"}

        {{AiClient.SchemaForAgent}}

        HOW TO WORK
        - Start with the query that most directly addresses the question.
        - You will be shown a sample of the rows and exact totals. Use those figures.
        - Issue another query only if you genuinely need more to answer. You have at
          most {{MaxSteps}} queries.
        - When you have enough, answer. Quote real numbers from the results; never
          estimate or invent one.
        - If the results show the question cannot be answered from this data, say so
          plainly rather than guessing.
        - Keep the answer under 150 words.
        """;

    public async Task<AgentRun> RunAsync(string question, CancellationToken ct)
    {
        var run = new AgentRun { Question = question, Model = _ai.Model };
        var sw = Stopwatch.StartNew();
        var budget = TimeSpan.FromSeconds(Math.Max(60, _opt.AgentBudgetSeconds));

        var messages = new List<AiChatMessage>
        {
            new("system", SystemPrompt),
            new("user", question),
        };

        try
        {
            for (var step = 1; step <= MaxSteps; step++)
            {
                if (sw.Elapsed > budget)
                {
                    run.Outcome = AgentOutcome.Timeout;
                    break;
                }

                // Leave the remaining budget as the per-call timeout so one slow
                // generation cannot overrun the whole allowance.
                var remaining = budget - sw.Elapsed;
                var reply = await _ai.ChatJsonAsync(messages, remaining, ct);
                messages.Add(new AiChatMessage("assistant", reply));

                var decision = Parse(reply);
                if (decision is null)
                {
                    run.Outcome = AgentOutcome.Error;
                    run.Error = "The model's reply could not be read. Try rephrasing the question.";
                    break;
                }

                if (decision.Action == "answer" || !string.IsNullOrWhiteSpace(decision.Answer))
                {
                    run.Answer = decision.Answer?.Trim();
                    run.Outcome = run.HasAnswer ? AgentOutcome.Answered : AgentOutcome.Error;
                    if (!run.HasAnswer)
                    {
                        run.Error = "The model finished without giving an answer.";
                    }
                    break;
                }

                if (string.IsNullOrWhiteSpace(decision.Sql))
                {
                    run.Outcome = AgentOutcome.Error;
                    run.Error = "The model asked to query but supplied no SQL.";
                    break;
                }

                // Same guard as the console. The agent gets no privilege the user
                // does not already have.
                var verdict = SqlGuard.Validate(decision.Sql);
                if (!verdict.Allowed)
                {
                    run.Steps.Add(new AgentStep
                    {
                        Number = step,
                        Kind = AgentStepKind.Refused,
                        Reasoning = decision.Reasoning,
                        Sql = decision.Sql,
                        Problem = verdict.Reason,
                    });
                    // Tell the model why, so it can correct itself rather than
                    // repeating the same rejected shape.
                    messages.Add(new AiChatMessage("user",
                        $"That query was refused: {verdict.Reason} Write a read-only SELECT instead."));
                    continue;
                }

                var stepSw = Stopwatch.StartNew();
                QueryResult result;
                try
                {
                    result = await _runner.RunAsync(
                        decision.Sql, new Dictionary<string, object?>(), _reporting.MaxRows, ct);
                }
                catch (Exception ex)
                {
                    stepSw.Stop();
                    run.Steps.Add(new AgentStep
                    {
                        Number = step,
                        Kind = AgentStepKind.Failed,
                        Reasoning = decision.Reasoning,
                        Sql = decision.Sql,
                        Problem = ex.Message,
                        Elapsed = stepSw.Elapsed,
                    });
                    messages.Add(new AiChatMessage("user",
                        $"That query failed: {Clip(ex.Message, 300)} Fix it or try a different approach."));
                    continue;
                }
                stepSw.Stop();

                run.Steps.Add(new AgentStep
                {
                    Number = step,
                    Kind = AgentStepKind.Query,
                    Reasoning = decision.Reasoning,
                    Sql = decision.Sql,
                    RowCount = result.Rows.Count,
                    Truncated = result.Truncated,
                    Elapsed = stepSw.Elapsed,
                    Result = result,
                });

                messages.Add(new AiChatMessage("user", Present(result)));

                if (step == MaxSteps)
                {
                    // Out of queries: ask for a conclusion from what it has rather
                    // than ending with nothing.
                    messages.Add(new AiChatMessage("user",
                        "That was your last query. Answer now from what you have, with the action \"answer\"."));
                    var final = await _ai.ChatJsonAsync(messages, budget - sw.Elapsed, ct);
                    var last = Parse(final);
                    run.Answer = last?.Answer?.Trim();
                    run.Outcome = run.HasAnswer ? AgentOutcome.Answered : AgentOutcome.StepLimit;
                }
            }

            if (run.Outcome == AgentOutcome.Error && run.Error is null && !run.HasAnswer)
            {
                run.Outcome = AgentOutcome.StepLimit;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Agent run failed.");
            run.Outcome = AgentOutcome.Error;
            run.Error = ex.Message;
        }

        sw.Stop();
        run.Elapsed = sw.Elapsed;
        _log.LogInformation(
            "Agent: outcome={Outcome} steps={Steps} elapsed={Sec:F1}s question={Question}",
            run.Outcome, run.Steps.Count, sw.Elapsed.TotalSeconds, Clip(question, 120));
        return run;
    }

    /// <summary>
    /// What the model is shown after a query: exact totals, then a small sample. The
    /// arithmetic is done here because a 7B model handed a table of numbers will
    /// restate them rather than analyse them, and gets sums wrong besides.
    /// </summary>
    private static string Present(QueryResult result)
    {
        var sb = new StringBuilder();
        sb.Append("Query returned ")
          .Append(result.Rows.Count.ToString("N0", CultureInfo.InvariantCulture))
          .Append(" row(s)");
        if (result.Truncated)
        {
            sb.Append(" (capped, so totals below are partial)");
        }
        sb.AppendLine(".");

        if (result.Rows.Count == 0)
        {
            sb.AppendLine("No rows matched. Consider a wider range or a different filter.");
            return sb.ToString();
        }

        sb.AppendLine();
        sb.AppendLine(ResultDigest.Build(result, "the query just run"));

        sb.Append("First ").Append(Math.Min(SampleRows, result.Rows.Count)).AppendLine(" row(s):");
        sb.AppendLine(string.Join(" | ", result.Columns.Select(c => c.Name)));
        foreach (var row in result.Rows.Take(SampleRows))
        {
            sb.AppendLine(string.Join(" | ", row.Select(Format)));
        }
        return sb.ToString();
    }

    private static string Format(object? v) => v switch
    {
        null => "",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => Clip(v.ToString() ?? "", 60),
    };

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private sealed class Decision
    {
        public string? Action { get; set; }
        public string? Reasoning { get; set; }
        public string? Sql { get; set; }
        public string? Answer { get; set; }
    }

    private Decision? Parse(string reply)
    {
        try
        {
            return JsonSerializer.Deserialize<Decision>(reply, Json);
        }
        catch (JsonException)
        {
            _log.LogWarning("Agent reply was not valid JSON: {Reply}", Clip(reply, 300));
            return null;
        }
    }
}
