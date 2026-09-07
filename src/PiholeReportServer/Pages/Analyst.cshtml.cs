using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using PiholeReportServer.Configuration;
using PiholeReportServer.Models;
using PiholeReportServer.Services;

namespace PiholeReportServer.Pages;

/// <summary>
/// A conversation with the analysis agent.
/// <para>
/// Carries the SqlAuthor role requirement: the agent writes and runs its own queries,
/// which is the same capability as the console, reached by a different route. Gating
/// it any lower would let a viewer run arbitrary SQL by asking nicely.
/// </para>
/// <para>
/// This was one question in, prose out, which made it behave like a query generator:
/// asked to "show me all the youtube traffic" it answered with a row count, and there
/// was no way to say "no, the list" without starting over. The turns are now kept, and
/// the rows the agent found are the headline output with the prose introducing them.
/// </para>
/// </summary>
[Authorize(Policy = AuthorizationPolicies.SqlAuthor)]
public sealed class AnalystModel : PageModel
{
    private readonly AiAgent _agent;
    private readonly AiOptions _opt;
    private readonly AiEndpointSelector _hosts;
    private readonly ConversationStore _conversations;
    private readonly ResultCache _results;
    private readonly AnalystMemoryStore _memory;

    public AnalystModel(
        AiAgent agent,
        IOptions<AiOptions> opt,
        AiEndpointSelector hosts,
        ConversationStore conversations,
        ResultCache results,
        AnalystMemoryStore memory)
    {
        _agent = agent;
        _opt = opt.Value;
        _hosts = hosts;
        _conversations = conversations;
        _results = results;
        _memory = memory;
    }

    [BindProperty(SupportsGet = true, Name = "c")]
    public string? ConversationId { get; set; }

    [BindProperty]
    [System.ComponentModel.DataAnnotations.StringLength(500)]
    public string? Question { get; set; }

    public Conversation? Conversation { get; private set; }

    /// <summary>
    /// Standing facts, shown on the page so it is visible what the assistant thinks
    /// it knows. An assistant that silently remembers a wrong fact is worse than one
    /// that remembers nothing, because every later answer inherits the error.
    /// </summary>
    public IReadOnlyList<AnalystFact> Facts { get; private set; } = [];

    public bool Enabled => _agent.Enabled;

    public int MaxSteps => _agent.MaxSteps;

    public int BudgetSeconds => _opt.AgentBudgetSeconds;

    public int PageSize => _results.PageSize;

    /// <summary>
    /// The model actually in use. This is not <c>Ai:Model</c>: when the preferred host
    /// is away the standby serves a smaller model, and the page would otherwise name
    /// one that is not answering.
    /// </summary>
    public string Model => _hosts.Current.Model;

    /// <summary>True while the preferred host is unreachable and the standby is serving.</summary>
    public bool UsingFallback => _hosts.Current.IsFallback;

    /// <summary>Host serving requests right now.</summary>
    public string ActiveEndpoint => _hosts.Current.Endpoint;

    private string? Owner => SavedReportStore.OwnerOid(User);

    /// <summary>Starting points, so the page is not a blank box.</summary>
    public static readonly string[] Examples =
    [
        "Show me the top domains Jason's phone is reaching this week",
        "List the YouTube and streaming traffic from yesterday",
        "Which devices generate the most advertising and tracking traffic?",
        "What was blocked most often in the last 7 days?",
        "Are any devices reaching domains categorised as malware?",
        "Compare weekday and weekend traffic by category",
    ];

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (Owner is not { } owner)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(ConversationId))
        {
            Conversation = _conversations.GetOrStart(owner, ConversationId);
            ConversationId = Conversation?.Id;
        }

        Facts = await _memory.ListAsync(owner, ct);
    }

    /// <summary>
    /// Answers one turn and returns JSON, so the transcript grows in place instead of
    /// the page reloading. That is most of what makes this feel like a conversation
    /// rather than a form submission.
    /// </summary>
    public async Task<IActionResult> OnPostAskAsync(CancellationToken ct)
    {
        if (!Enabled)
        {
            return Fail("The AI assistant is not configured.");
        }
        if (Owner is not { } owner)
        {
            return Fail("Could not identify you from the sign-in token.");
        }

        var question = Question?.Trim();
        if (string.IsNullOrWhiteSpace(question))
        {
            return Fail("Ask a question first.");
        }
        if (question.Length > 500)
        {
            return Fail("Questions are limited to 500 characters.");
        }

        var convo = _conversations.GetOrStart(owner, ConversationId);
        if (convo is null)
        {
            return Fail("That conversation belongs to someone else.");
        }

        var facts = await _memory.ListAsync(owner, ct);
        var run = await _agent.RunAsync(question, convo.Turns, AnalystMemoryStore.Render(facts), ct);

        // Persist a fact the model was just taught. Done here rather than in the agent
        // because the row is scoped to the signed-in identity, which the page owns.
        string? memoryNote = null;
        if (run.MemoryRequest is { } req && !string.IsNullOrWhiteSpace(req.Subject))
        {
            if (req.Forget)
            {
                memoryNote = await _memory.ForgetAsync(owner, req.Subject!, ct)
                    ? $"Forgotten: {req.Subject}"
                    : $"I had nothing remembered for \"{req.Subject}\".";
            }
            else
            {
                var stored = await _memory.RememberAsync(
                    owner, SavedReportStore.OwnerName(User),
                    req.Kind, req.Subject!, req.Target, req.Fact ?? req.Subject!, ct);
                memoryNote = stored.Ok ? $"Remembered: {stored.Fact}" : stored.Problem;
            }
            facts = await _memory.ListAsync(owner, ct);
        }
        else if (facts.Count > 0)
        {
            await _memory.MarkUsedAsync(owner, ct);
        }

        var turn = new ConversationTurn
        {
            Question = question,
            Answer = run.Answer,
            Outcome = run.Outcome,
            Error = run.Error,
            Elapsed = run.Elapsed,
            Model = run.Model,
        };
        turn.Steps.AddRange(run.Steps);
        turn.ModelReplies.AddRange(run.ModelReplies);
        turn.Observations.AddRange(run.Observations);

        // The rows ARE the answer for anything phrased as "show me", so they get the
        // same caching the report pages use - searching and paging included. The agent
        // can legitimately return thousands of rows and re-running its SQL per page
        // would scan the fact table again.
        if (run.AnswerTable is { } table)
        {
            turn.Result = table;
            turn.ResultToken = _results.Store(owner, table, $"Analyst: {question}");
        }

        convo.Turns.Add(turn);
        _conversations.Save(convo);

        return new JsonResult(new
        {
            ok = true,
            conversationId = convo.Id,
            answer = turn.Answer,
            error = turn.Error,
            outcome = turn.Outcome.ToString(),
            model = turn.Model,
            elapsedSeconds = Math.Round(turn.Elapsed.TotalSeconds, 1),
            queries = turn.SuccessfulQueries,
            memoryNote,
            facts = facts.Select(f => new { f.Id, f.Kind, f.Subject, f.Target, f.Fact }),
            table = Describe(turn),
            steps = turn.Steps.Select(st => new
            {
                number = st.Number,
                kind = st.Kind.ToString(),
                reasoning = st.Reasoning,
                sql = st.Sql,
                problem = st.Problem,
                rowCount = st.RowCount,
                milliseconds = (int)st.Elapsed.TotalMilliseconds,
            }),
        });
    }

    /// <summary>Forgets one fact from the panel, for correcting a wrong one directly.</summary>
    public async Task<IActionResult> OnPostForgetAsync(int id, CancellationToken ct)
    {
        if (Owner is { } owner)
        {
            await _memory.ForgetAsync(owner, id, ct);
        }
        return RedirectToPage(new { c = ConversationId });
    }

    /// <summary>Adds a fact from the panel, for teaching one without a conversation.</summary>
    public async Task<IActionResult> OnPostRememberAsync(
        string? subject, string? target, string? note, CancellationToken ct)
    {
        if (Owner is { } owner && !string.IsNullOrWhiteSpace(subject))
        {
            var isDevice = !string.IsNullOrWhiteSpace(target);
            await _memory.RememberAsync(
                owner, SavedReportStore.OwnerName(User),
                isDevice ? "device" : "note",
                subject!, target,
                string.IsNullOrWhiteSpace(note)
                    ? (isDevice ? $"{subject} is the device {target}" : subject!)
                    : note!,
                ct);
        }
        return RedirectToPage(new { c = ConversationId });
    }

    public IActionResult OnPostClear()
    {
        if (Owner is { } owner && !string.IsNullOrWhiteSpace(ConversationId))
        {
            _conversations.Clear(owner, ConversationId);
        }
        return RedirectToPage();
    }

    private static JsonResult Fail(string message) =>
        new(new { ok = false, error = message });

    private object? Describe(ConversationTurn turn)
    {
        if (turn.Result is not { } r)
        {
            return null;
        }

        return new
        {
            token = turn.ResultToken,
            columns = r.Columns.Select(c => new { name = c.Name, numeric = IsNumeric(c) }),
            // Only the first page travels with the reply; the rest is fetched from the
            // cache by the shared result-table script, exactly as elsewhere.
            rows = r.Rows.Take(PageSize).Select(row => row.Select(Cell).ToArray()),
            totalRows = r.Rows.Count,
            truncated = r.Truncated,
            serverPaged = r.Rows.Count > PageSize,
        };
    }

    private static bool IsNumeric(QueryColumn c) =>
        c.ClrType is "Int32" or "Int64" or "Int16" or "Byte"
                   or "Decimal" or "Double" or "Single";

    private static string? Cell(object? v) => v switch
    {
        null => null,
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };
}
