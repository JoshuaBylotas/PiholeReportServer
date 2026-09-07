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
}
