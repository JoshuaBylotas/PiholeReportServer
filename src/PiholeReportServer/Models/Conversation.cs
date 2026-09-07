namespace PiholeReportServer.Models;

/// <summary>
/// One exchange in an Analyst conversation: what was asked, and everything the
/// agent did to answer it.
/// </summary>
public sealed class ConversationTurn
{
    public required string Question { get; init; }

    /// <summary>The prose reply. Short by design — the table carries the data.</summary>
    public string? Answer { get; set; }

    public AgentOutcome Outcome { get; set; }

    public string? Error { get; set; }

    /// <summary>
    /// The queries run, kept so the answer can be audited. A conclusion from a local
    /// model is worth little if the reasoning behind it cannot be checked.
    /// </summary>
    public List<AgentStep> Steps { get; init; } = [];

    /// <summary>
    /// Token for the rows shown under the answer, held in <c>ResultCache</c>. The
    /// table is the answer for anything phrased as "show me" or "list", so it is a
    /// first-class part of the turn rather than a debug view.
    /// </summary>
    public string? ResultToken { get; set; }

    /// <summary>Columns of that result, so the table can render before paging.</summary>
    public QueryResult? Result { get; set; }

    /// <summary>
    /// A chart over <see cref="Result"/>, already checked against its columns. Null
    /// when the model did not ask for one or the data could not support it — two rows
    /// is the minimum, and a chart of one row tells nobody anything.
    /// </summary>
    public ResolvedChart? Chart { get; set; }

    public TimeSpan Elapsed { get; set; }

    public string? Model { get; set; }

    public DateTime AskedUtc { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// The raw JSON the model emitted, replayed to it on the next turn so it can see
    /// its own earlier decisions. Not shown to the user.
    /// </summary>
    public List<string> ModelReplies { get; init; } = [];

    /// <summary>
    /// The digests the model was shown after each query, replayed with the above so a
    /// follow-up like "and the second one?" has the figures to refer to.
    /// </summary>
    public List<string> Observations { get; init; } = [];

    public bool HasAnswer => !string.IsNullOrWhiteSpace(Answer);

    public int SuccessfulQueries => Steps.Count(s => s.Kind == AgentStepKind.Query);
}

/// <summary>A running conversation on the Analyst page, owned by one user.</summary>
public sealed class Conversation
{
    public required string Id { get; init; }

    public required string OwnerOid { get; init; }

    public List<ConversationTurn> Turns { get; } = [];

    public DateTime StartedUtc { get; } = DateTime.UtcNow;
}
