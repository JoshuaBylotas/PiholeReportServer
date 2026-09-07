namespace PiholeReportServer.Models;

public enum AgentStepKind
{
    /// <summary>The model asked to run a query.</summary>
    Query,

    /// <summary>The query was refused by the guard and never reached the database.</summary>
    Refused,

    /// <summary>The query ran but the database rejected or failed it.</summary>
    Failed,
}

/// <summary>
/// One turn of the agent loop, kept so the whole trace can be shown. An answer the
/// user cannot audit is worth very little when the reasoning came from a 7B model,
/// so every query it ran and every row count it saw is on display.
/// </summary>
public sealed class AgentStep
{
    public required int Number { get; init; }

    public required AgentStepKind Kind { get; init; }

    /// <summary>The model's stated reason for this query, in its own words.</summary>
    public string? Reasoning { get; init; }

    public string? Sql { get; init; }

    /// <summary>Why the guard refused it, or what the database said.</summary>
    public string? Problem { get; init; }

    public int RowCount { get; init; }

    public bool Truncated { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>The result, kept so the user can see exactly what the model saw.</summary>
    public QueryResult? Result { get; init; }
}

public enum AgentOutcome
{
    Answered,

    /// <summary>Hit the step budget without concluding.</summary>
    StepLimit,

    /// <summary>Hit the wall-clock budget.</summary>
    Timeout,

    /// <summary>The model or the endpoint failed.</summary>
    Error,
}

/// <summary>
/// The full record of one question: what was asked, every query run on the way, and
/// the conclusion.
/// </summary>
public sealed class AgentRun
{
    public required string Question { get; init; }

    public List<AgentStep> Steps { get; } = [];

    public string? Answer { get; set; }

    public AgentOutcome Outcome { get; set; } = AgentOutcome.Error;

    public string? Error { get; set; }

    public TimeSpan Elapsed { get; set; }

    public string? Model { get; set; }

    /// <summary>Queries that actually reached the database and returned rows.</summary>
    public int SuccessfulQueries => Steps.Count(s => s.Kind == AgentStepKind.Query);

    public bool HasAnswer => !string.IsNullOrWhiteSpace(Answer);

    /// <summary>
    /// The model's raw JSON replies, kept so the next turn of a conversation can
    /// replay them. Not shown to the user.
    /// </summary>
    public List<string> ModelReplies { get; } = [];

    /// <summary>The digests the model was shown, replayed alongside the above.</summary>
    public List<string> Observations { get; } = [];

    /// <summary>
    /// Set when the turn was the user teaching it something ("the Pixel is Jason's
    /// phone") rather than asking a question. The page persists it.
    /// </summary>
    public AgentMemoryRequest? MemoryRequest { get; set; }

    /// <summary>
    /// A chart the model asked for over <see cref="AnswerTable"/>. Validated against
    /// the actual columns before it reaches the browser, because a chart naming a
    /// column that is not there would render as an empty box with no explanation.
    /// </summary>
    public ChartRequest? Chart { get; set; }

    /// <summary>
    /// The rows to show under the answer: the last query that actually returned any.
    /// <para>
    /// This is the point of the page for anything phrased as "show me" or "list" —
    /// the prose is an introduction to this table, not a replacement for it. The last
    /// productive query is the right one because the agent narrows as it goes, so its
    /// final query is the one that addressed the question.
    /// </para>
    /// </summary>
    public QueryResult? AnswerTable =>
        Steps.LastOrDefault(s => s.Kind == AgentStepKind.Query && s.Result is { Rows.Count: > 0 })?.Result;
}

/// <summary>
/// A request from the model to remember or forget a standing fact, raised when the
/// user is telling it something rather than asking. The agent does not write it
/// itself: persistence is scoped to the signed-in identity, which the page owns.
/// </summary>
public sealed class AgentMemoryRequest
{
    public bool Forget { get; init; }

    /// <summary>"device" for an alias that becomes a filter, "note" otherwise.</summary>
    public string Kind { get; init; } = "note";

    public string? Subject { get; init; }

    /// <summary>Hostname, IP or MAC the subject refers to. Required for a device.</summary>
    public string? Target { get; init; }

    public string? Fact { get; init; }
}

/// <summary>
/// A chart the model asked for over the answer table.
/// <para>
/// <see cref="Label"/> and <see cref="Value"/> are column names from the query, not
/// indexes: the model reliably names a column it just selected and unreliably counts
/// positions. <see cref="Resolve"/> turns them into indexes and rejects anything that
/// does not fit, so a bad request degrades to "no chart" rather than a blank panel.
/// </para>
/// </summary>
public sealed class ChartRequest
{
    public string? Type { get; set; }

    public string? Label { get; set; }

    public string? Value { get; set; }

    private static readonly string[] Numeric =
        ["Int32", "Int64", "Int16", "Byte", "Decimal", "Double", "Single"];

    /// <summary>Chart types the browser can draw. Anything else falls back to a bar.</summary>
    public string NormalisedType => Type?.Trim().ToLowerInvariant() switch
    {
        "line" => "line",
        "pie" => "pie",
        _ => "bar",
    };

    /// <summary>
    /// Resolves the named columns against a result, or returns null when the request
    /// cannot be honoured. A pie of 40 slices and a chart of a single row are both
    /// refused: they are noise, not information.
    /// </summary>
    public ResolvedChart? Resolve(QueryResult result)
    {
        if (result.Rows.Count < 2)
        {
            return null;
        }

        var labelIndex = IndexOf(result, Label);
        var valueIndex = IndexOf(result, Value);

        // Fall back to the obvious pair rather than giving up: the first text column
        // and the first numeric one is what almost every one of these queries returns.
        if (labelIndex < 0)
        {
            labelIndex = FirstMatching(result, numeric: false);
        }
        if (valueIndex < 0)
        {
            valueIndex = FirstMatching(result, numeric: true);
        }
        if (labelIndex < 0 || valueIndex < 0 || labelIndex == valueIndex)
        {
            return null;
        }
        if (!Numeric.Contains(result.Columns[valueIndex].ClrType))
        {
            return null;
        }

        var type = NormalisedType;
        // A pie needs to be readable; past a handful of slices a bar is honest and a
        // pie is decoration.
        if (type == "pie" && result.Rows.Count > 8)
        {
            type = "bar";
        }

        return new ResolvedChart(type, labelIndex, valueIndex,
            result.Columns[labelIndex].Name, result.Columns[valueIndex].Name);
    }

    private static int IndexOf(QueryResult result, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return -1;
        }
        for (var i = 0; i < result.Columns.Count; i++)
        {
            if (string.Equals(result.Columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    private static int FirstMatching(QueryResult result, bool numeric)
    {
        for (var i = 0; i < result.Columns.Count; i++)
        {
            if (Numeric.Contains(result.Columns[i].ClrType) == numeric)
            {
                return i;
            }
        }
        return -1;
    }
}

/// <summary>A chart request checked against a real result and safe to render.</summary>
public sealed record ResolvedChart(
    string Type, int LabelIndex, int ValueIndex, string LabelColumn, string ValueColumn);
