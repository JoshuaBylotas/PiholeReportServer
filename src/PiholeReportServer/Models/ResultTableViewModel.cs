namespace PiholeReportServer.Models;

/// <summary>
/// What the shared result table needs.
/// <para>
/// When <see cref="Token"/> is set, only the first page is rendered and the browser
/// asks the server for further pages — that is what makes a 100,000-row cap usable.
/// When it is null the whole result is rendered inline, which is right for the small
/// per-step samples shown in an agent trace.
/// </para>
/// </summary>
public sealed class ResultTableViewModel
{
    public required QueryResult Result { get; init; }

    /// <summary>Null means "render it all, no server paging".</summary>
    public string? Token { get; init; }

    public int PageSize { get; init; } = 100;

    public bool ServerPaged => Token is not null && Result.Rows.Count > PageSize;

    /// <summary>Rows to render now: one page when server-paged, otherwise everything.</summary>
    public IEnumerable<object?[]> VisibleRows =>
        ServerPaged ? Result.Rows.Take(PageSize) : Result.Rows;

    public int PageCount =>
        Math.Max(1, (int)Math.Ceiling(Result.Rows.Count / (double)Math.Max(1, PageSize)));

    public static ResultTableViewModel For(QueryResult result, string? token, int pageSize) =>
        new() { Result = result, Token = token, PageSize = pageSize };

    /// <summary>For an agent step or anywhere a transient result is shown in full.</summary>
    public static ResultTableViewModel Inline(QueryResult result) =>
        new() { Result = result, Token = null, PageSize = int.MaxValue };
}
