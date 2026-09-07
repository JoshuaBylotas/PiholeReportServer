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

    private string SystemPrompt => SystemPromptWith("");

    private string SystemPromptWith(string memory) => $$$"""
        You are talking with the owner of a home network about their DNS traffic. You
        have read-only SQL access to a Pi-hole warehouse. Be conversational: you are
        an assistant they are chatting with, not a query generator.

        Reply with ONLY a JSON object, one of two shapes:

          {"action":"query","reasoning":"why this query","sql":"SELECT ..."}
          {"action":"answer","answer":"what you found, in a sentence or two"}

        Always include the "action" field. A reply without it cannot be read.

        An answer may also ask for a chart of the table, when the shape suits one:

          {"action":"answer","answer":"...",
           "chart":{"type":"bar","label":"device","value":"queries"}}

        type is "bar" (comparing categories), "line" (a series over time) or
        "pie" (parts of a whole, only when there are few slices). label and value
        must be COLUMN NAMES from your last query, label being the text axis and
        value a number. Omit "chart" entirely when a table is clearer - a chart of
        one row, or of forty unrelated ones, helps nobody.

        {{{AiClient.SchemaForAgent}}}

        {{{memory}}}

        HOW TO WORK
        - Start with the query that most directly addresses what they asked.
        - You will be shown a sample of the rows and exact totals. Use those figures.
        - Issue another query only if you genuinely need more. You have at most
          {{{MaxSteps}}} queries.
        - When you have enough, answer.

        WHAT AN ANSWER SHOULD BE
        - The rows from your last query are shown to them as a table, in full, under
          your answer. They can read it. So do not list the rows back to them and do
          not recite every value.
        - Instead say what the table shows: the headline figure, what stands out, and
          anything surprising. Two or three sentences is usually right, and never
          more than 150 words.
        - Quote real numbers from the results. Never estimate or invent one.
        - If they asked to SEE something, the table is the answer and your job is a
          short introduction to it. Make sure your final query returns the rows they
          wanted rather than a count of them.
        - If the data cannot answer the question, say so plainly and say what is
          missing, rather than guessing.

        FOLLOWING UP
        - Earlier turns of this conversation are above. "That device", "those
          domains", "the same but for last month" all refer back to them - resolve
          the reference yourself and query accordingly rather than asking what they
          meant.
        - If a request really is ambiguous, make the most reasonable assumption,
          answer, and say which assumption you made.
        """;

    public Task<AgentRun> RunAsync(string question, CancellationToken ct) =>
        RunAsync(question, [], "", ct);

    public Task<AgentRun> RunAsync(
        string question, IReadOnlyList<ConversationTurn> history, CancellationToken ct) =>
        RunAsync(question, history, "", ct);

    /// <summary>
    /// Answers a question, optionally continuing an existing conversation.
    /// <para>
    /// <paramref name="history"/> is replayed ahead of the question so a follow-up
    /// like "now just for that device" resolves without the user restating anything.
    /// </para>
    /// </summary>
    /// <param name="memory">
    /// Standing facts rendered by <see cref="AnalystMemoryStore.Render"/>, so the model
    /// knows things like which hostname "Jason's phone" refers to. Empty when there are
    /// none, in which case the prompt simply has no memory section.
    /// </param>
    public async Task<AgentRun> RunAsync(
        string question,
        IReadOnlyList<ConversationTurn> history,
        string memory,
        CancellationToken ct)
    {
        var run = new AgentRun { Question = question, Model = _ai.Model };
        var sw = Stopwatch.StartNew();
        var budget = TimeSpan.FromSeconds(Math.Max(60, _opt.AgentBudgetSeconds));

        var messages = new List<AiChatMessage> { new("system", SystemPromptWith(memory)) };

        // Replay earlier turns as the exchange they actually were: the question, the
        // model's own JSON decisions, and the digests it was shown. Feeding back its
        // own words rather than a summary is what lets it refer to them accurately.
        foreach (var turn in history)
        {
            messages.Add(new AiChatMessage("user", turn.Question));
            for (var i = 0; i < turn.ModelReplies.Count; i++)
            {
                messages.Add(new AiChatMessage("assistant", turn.ModelReplies[i]));
                if (i < turn.Observations.Count)
                {
                    messages.Add(new AiChatMessage("user", turn.Observations[i]));
                }
            }
        }

        messages.Add(new AiChatMessage("user", question));

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
                run.ModelReplies.Add(reply);

                var decision = Parse(reply);
                if (decision is null)
                {
                    run.Outcome = AgentOutcome.Error;
                    run.Error = "The model's reply could not be read as JSON. Try rephrasing the question.";
                    break;
                }

                // A reply with neither an answer nor SQL is a dead end. Saying so and
                // asking again beats returning nothing, which is what used to happen:
                // the loop would fall through to the "no SQL" error and stop.
                if (string.IsNullOrWhiteSpace(decision.Answer) &&
                    string.IsNullOrWhiteSpace(decision.Sql) &&
                    string.IsNullOrWhiteSpace(decision.Fact))
                {
                    messages.Add(new AiChatMessage("user",
                        "That reply had neither \"sql\" nor \"answer\". Reply with one of the two " +
                        "shapes described, including the \"action\" field."));
                    continue;
                }

                // Being told a fact is not a query. Recorded on the run and persisted by
                // the caller, which owns the signed-in identity the row is scoped to.
                if (decision.Action == "remember" || decision.Action == "forget")
                {
                    run.MemoryRequest = new AgentMemoryRequest
                    {
                        Forget = decision.Action == "forget",
                        Kind = string.IsNullOrWhiteSpace(decision.Kind) ? "note" : decision.Kind!,
                        Subject = decision.Subject,
                        Target = decision.Target,
                        Fact = string.IsNullOrWhiteSpace(decision.Fact) ? decision.Answer : decision.Fact,
                    };
                    run.Answer = decision.Answer?.Trim();
                    run.Outcome = AgentOutcome.Answered;
                    break;
                }

                if (decision.Action == "answer" || !string.IsNullOrWhiteSpace(decision.Answer))
                {
                    run.Answer = decision.Answer?.Trim();
                    run.Chart = decision.Chart;
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

                var observation = Present(result);
                messages.Add(new AiChatMessage("user", observation));
                run.Observations.Add(observation);

                if (step == MaxSteps)
                {
                    // Out of queries: ask for a conclusion from what it has rather
                    // than ending with nothing.
                    messages.Add(new AiChatMessage("user",
                        "That was your last query. Answer now from what you have, with the action \"answer\"."));
                    var final = await _ai.ChatJsonAsync(messages, budget - sw.Elapsed, ct);
                    run.ModelReplies.Add(final);
                    var last = Parse(final);
                    run.Answer = last?.Answer?.Trim();
                    run.Chart = last?.Chart;
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

        // Present when the model is being told a standing fact rather than asked a
        // question: "the Pixel is Jason's phone".
        public string? Kind { get; set; }
        public string? Subject { get; set; }
        public string? Target { get; set; }
        public string? Fact { get; set; }

        /// <summary>Optional chart request accompanying an answer.</summary>
        public ChartRequest? Chart { get; set; }
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
