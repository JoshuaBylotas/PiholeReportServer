namespace PiholeReportServer.Models;

public sealed record QueryColumn(string Name, string ClrType);

/// <summary>
/// A materialised, row-capped result set ready for rendering or export.
/// </summary>
public sealed class QueryResult
{
    public required IReadOnlyList<QueryColumn> Columns { get; init; }

    public required IReadOnlyList<object?[]> Rows { get; init; }

    /// <summary>True when the row cap stopped the read before the end of the result.</summary>
    public bool Truncated { get; init; }

    public int RowCap { get; init; }

    public TimeSpan Elapsed { get; init; }

    public static QueryResult Empty { get; } = new()
    {
        Columns = [],
        Rows = [],
    };
}
