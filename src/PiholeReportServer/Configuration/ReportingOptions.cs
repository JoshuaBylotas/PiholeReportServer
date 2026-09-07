namespace PiholeReportServer.Configuration;

public sealed class ReportingOptions
{
    public const string SectionName = "Reporting";

    /// <summary>
    /// Maximum rows a single query may materialise. The result is held server-side by
    /// ResultCache and paged from there, so this is bounded by worker memory rather
    /// than by what a browser can render.
    /// </summary>
    public int MaxRows { get; set; } = 100_000;

    /// <summary>
    /// Rows per page. Only this many ever reach the browser at once, which is what
    /// makes a 100,000-row cap usable.
    /// </summary>
    public int PageSize { get; set; } = 100;

    /// <summary>Maximum rows written to a CSV export.</summary>
    public int MaxExportRows { get; set; } = 250_000;

    /// <summary>
    /// Folder holding the pre-canned report definitions, relative to the
    /// content root unless rooted.
    /// </summary>
    public string ReportsPath { get; set; } = "Reports";

    /// <summary>
    /// Default window applied when a report or the builder is opened without an
    /// explicit date range.
    /// </summary>
    public int DefaultRangeDays { get; set; } = 7;

    /// <summary>
    /// Whether the raw-SQL console is reachable at all. Even when true it stays
    /// gated behind the SqlAuthor authorization policy.
    /// </summary>
    public bool EnableRawSql { get; set; } = true;
}
