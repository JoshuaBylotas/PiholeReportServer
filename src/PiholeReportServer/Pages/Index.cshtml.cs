using Microsoft.AspNetCore.Mvc.RazorPages;
using PiholeReportServer.Data;
using PiholeReportServer.Services;

namespace PiholeReportServer.Pages;

public sealed class IndexModel : PageModel
{
    private readonly ReportRunner _runner;
    private readonly ReportCatalog _catalog;
    private readonly ILogger<IndexModel> _log;

    public IndexModel(ReportRunner runner, ReportCatalog catalog, ILogger<IndexModel> log)
    {
        _runner = runner;
        _catalog = catalog;
        _log = log;
    }

    public long TotalQueries { get; private set; }
    public long DistinctDomains { get; private set; }
    public long DistinctClients { get; private set; }
    public DateTime? NewestQuery { get; private set; }
    public DateTime? OldestQuery { get; private set; }
    public int ReportCount { get; private set; }
    public string? LoadError { get; private set; }

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
        }
    }
}
