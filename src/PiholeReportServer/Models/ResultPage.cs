namespace PiholeReportServer.Models;

/// <summary>
/// One page of a cached result set. Deliberately small: at 100 rows a page this is a
/// few kilobytes of JSON, whatever the size of the underlying result.
/// </summary>
public sealed class ResultPage
{
    public required string Token { get; init; }

    public required IReadOnlyList<QueryColumn> Columns { get; init; }

    public required IReadOnlyList<object?[]> Rows { get; init; }

    /// <summary>Zero-based.</summary>
    public required int Page { get; init; }

    public required int PageCount { get; init; }

    public required int PageSize { get; init; }

    /// <summary>Rows matching the current search.</summary>
    public required int MatchingRows { get; init; }

    /// <summary>Rows in the result before any search was applied.</summary>
    public required int TotalRows { get; init; }

    /// <summary>True when the query hit the row cap, so totals are partial.</summary>
    public required bool Truncated { get; init; }

    public required int RowCap { get; init; }

    public string? Search { get; init; }

    public bool IsFiltered => MatchingRows != TotalRows;

    public int FirstRowNumber => MatchingRows == 0 ? 0 : (Page * PageSize) + 1;

    public int LastRowNumber => Math.Min((Page + 1) * PageSize, MatchingRows);
}
