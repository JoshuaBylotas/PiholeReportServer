using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using PiholeReportServer.Configuration;
using PiholeReportServer.Models;
using PiholeReportServer.Services;

namespace PiholeReportServer.Pages.Query;

/// <summary>
/// Free-text SQL console. Gated by the SqlAuthor policy on top of the
/// site-wide Entra ID requirement, and every statement passes through
/// <see cref="SqlGuard"/> before it reaches the database.
/// </summary>
[Authorize(Policy = AuthorizationPolicies.SqlAuthor)]
public sealed class SqlModel : PageModel
{
    private readonly ReportRunner _runner;
    private readonly ReportingOptions _reporting;
    private readonly ILogger<SqlModel> _log;

    public SqlModel(
        ReportRunner runner,
        IOptions<ReportingOptions> reporting,
        ILogger<SqlModel> log)
    {
        _runner = runner;
        _reporting = reporting.Value;
        _log = log;
    }

    [BindProperty]
    public string? Sql { get; set; }

    public QueryResult? Result { get; private set; }

    public string? ErrorMessage { get; private set; }

    public bool Rejected { get; private set; }

    public int MaxRows => _reporting.MaxRows;

    public bool Enabled => _reporting.EnableRawSql;

    private const string Sample = """
        -- Busiest clients over the last 24 hours, resolved to host names
        SELECT TOP (50)
               COALESCE(dc.hostname, q.client) AS client,
               COUNT_BIG(*)                    AS queries,
               COUNT(DISTINCT q.domain)        AS distinct_domains
        FROM dbo.PiholeQueries AS q
             LEFT JOIN dbo.DimClient AS dc ON dc.ip = q.client
        WHERE q.ts >= DATEADD(hour, -24, SYSUTCDATETIME())
        GROUP BY COALESCE(dc.hostname, q.client)
        ORDER BY queries DESC
        """;

    public void OnGet() => Sql ??= Sample;

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!Enabled)
        {
            ErrorMessage = "The SQL console is disabled by configuration (Reporting:EnableRawSql).";
            return Page();
        }

        var verdict = SqlGuard.Validate(Sql);
        if (!verdict.Allowed)
        {
            Rejected = true;
            ErrorMessage = verdict.Reason;
            _log.LogInformation("SQL console rejected a statement from {User}: {Reason}",
                User.Identity?.Name, verdict.Reason);
            return Page();
        }

        try
        {
            _log.LogInformation("SQL console executing for {User}.", User.Identity?.Name);
            Result = await _runner.RunAsync(Sql!, new Dictionary<string, object?>(), ct: ct);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostExportAsync(CancellationToken ct)
    {
        if (!Enabled)
        {
            ErrorMessage = "The SQL console is disabled by configuration (Reporting:EnableRawSql).";
            return Page();
        }

        var verdict = SqlGuard.Validate(Sql);
        if (!verdict.Allowed)
        {
            Rejected = true;
            ErrorMessage = verdict.Reason;
            return Page();
        }

        try
        {
            var result = await _runner.RunAsync(
                Sql!, new Dictionary<string, object?>(), _reporting.MaxExportRows, ct);

            var stream = new MemoryStream();
            await CsvExporter.WriteAsync(stream, result, ct);
            stream.Position = 0;

            return File(stream, "text/csv", CsvExporter.FileName("query"));
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return Page();
        }
    }
}
