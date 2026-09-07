using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PiholeReportServer.Data;
using PiholeReportServer.Services;

namespace PiholeReportServer.Pages;

public sealed class IndexModel : PageModel
{
    private readonly ReportRunner _runner;
    private readonly ReportCatalog _catalog;
    private readonly NightlyFindingStore _findings;
    private readonly ILogger<IndexModel> _log;

    public IndexModel(
        ReportRunner runner,
        ReportCatalog catalog,
        NightlyFindingStore findings,
        ILogger<IndexModel> log)
    {
        _runner = runner;
        _catalog = catalog;
        _findings = findings;
        _log = log;
    }

    public long TotalQueries { get; private set; }
    public long DistinctDomains { get; private set; }
    public long DistinctClients { get; private set; }
    public DateTime? NewestQuery { get; private set; }
    public DateTime? OldestQuery { get; private set; }
    public int ReportCount { get; private set; }
    public string? LoadError { get; private set; }

    /// <summary>Traffic by content type, from dbo.DomainCategory.</summary>
    public IReadOnlyList<(string Category, long Queries, double Share)> Categories { get; private set; } = [];

    public double CategorisedShare { get; private set; }

    public IReadOnlyList<NightlyFinding> Findings { get; private set; } = [];

    private const string OverviewSql = """
        SELECT
            (SELECT COUNT_BIG(*)               FROM dbo.PiholeQueries) AS total_queries,
            (SELECT COUNT_BIG(DISTINCT domain) FROM dbo.PiholeQueries
                 WHERE ts >= DATEADD(day, -7, SYSUTCDATETIME()))       AS domains_7d,
            (SELECT COUNT_BIG(DISTINCT client) FROM dbo.PiholeQueries
                 WHERE ts >= DATEADD(day, -7, SYSUTCDATETIME()))       AS clients_7d,
            (SELECT MAX(ts)                    FROM dbo.PiholeQueries) AS newest,
            (SELECT MIN(ts)                    FROM dbo.PiholeQueries) AS oldest;
        """;

    public async Task OnGetAsync(CancellationToken ct)
    {
        ReportCount = _catalog.All().Count;

        try
        {
            var r = await _runner.RunAsync(OverviewSql, new Dictionary<string, object?>(), rowCap: 1, ct);
            if (r.Rows.Count == 1)
            {
                var row = r.Rows[0];
                TotalQueries = Convert.ToInt64(row[0] ?? 0L);
                DistinctDomains = Convert.ToInt64(row[1] ?? 0L);
                DistinctClients = Convert.ToInt64(row[2] ?? 0L);
                NewestQuery = row[3] as DateTime?;
                OldestQuery = row[4] as DateTime?;
            }
        }
        catch (Exception ex)
        {
            // The overview is informational; a database problem should still
            // leave the rest of the site usable and point at Diagnostics.
            _log.LogWarning(ex, "Overview statistics could not be loaded.");
            LoadError = ex.Message;
            return;
        }

        await LoadCategoriesAsync(ct);
        await LoadFindingsAsync(ct);
    }

    private const string CategorySql = """
        WITH v AS (
            SELECT domain, COUNT_BIG(*) AS queries
            FROM dbo.PiholeQueries
            WHERE domain <> '' AND ts >= DATEADD(day, -30, SYSUTCDATETIME())
            GROUP BY domain
        )
        SELECT TOP 10
               c.category,
               SUM(v.queries)                                              AS queries,
               CAST(100.0 * SUM(v.queries) / NULLIF((SELECT SUM(queries) FROM v), 0) AS float) AS share
        FROM v JOIN dbo.DomainCategory AS c ON c.domain = v.domain
        GROUP BY c.category
        ORDER BY SUM(v.queries) DESC;
        """;

    private async Task LoadCategoriesAsync(CancellationToken ct)
    {
        try
        {
            var r = await _runner.RunAsync(CategorySql, new Dictionary<string, object?>(), 32, ct);
            var list = new List<(string, long, double)>();
            var total = 0d;
            foreach (var row in r.Rows)
            {
                var share = row[2] is null ? 0d : Convert.ToDouble(row[2]);
                total += share;
                list.Add((row[0]?.ToString() ?? "unknown", Convert.ToInt64(row[1] ?? 0L), share));
            }
            Categories = list;
            CategorisedShare = total;
        }
        catch (Exception ex)
        {
            // Categorisation is an enhancement; the overview must still render without it.
            _log.LogWarning(ex, "Category breakdown unavailable.");
        }
    }

    private async Task LoadFindingsAsync(CancellationToken ct)
    {
        try
        {
            Findings = await _findings.GetRecentAsync(days: 7, includeDismissed: false, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Nightly findings unavailable.");
        }
    }

    public async Task<IActionResult> OnPostDismissAsync(int id, CancellationToken ct)
    {
        await _findings.DismissAsync(id, User.Identity?.Name, ct);
        return RedirectToPage();
    }
}
