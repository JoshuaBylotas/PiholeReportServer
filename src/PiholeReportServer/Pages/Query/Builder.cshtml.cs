using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using PiholeReportServer.Configuration;
using PiholeReportServer.Models;
using PiholeReportServer.Services;

namespace PiholeReportServer.Pages.Query;

public sealed class BuilderModel : PageModel
{
    private readonly ReportRunner _runner;
    private readonly ClientDirectory _clients;
    private readonly ReportingOptions _reporting;
    private readonly ILogger<BuilderModel> _log;

    public BuilderModel(
        ReportRunner runner,
        ClientDirectory clients,
        IOptions<ReportingOptions> reporting,
        ILogger<BuilderModel> log)
    {
        _runner = runner;
        _clients = clients;
        _reporting = reporting.Value;
        _log = log;
    }

    [BindProperty]
    public BuilderSpec Spec { get; set; } = new();

    public QueryResult? Result { get; private set; }

    /// <summary>The composed SQL, shown so the builder doubles as a teaching tool.</summary>
    public string? GeneratedSql { get; private set; }

    public string? ErrorMessage { get; private set; }

    /// <summary>Known devices for the client picker; empty if DimClient is unavailable.</summary>
    public IReadOnlyList<ClientEntry> Clients { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Spec = new BuilderSpec
        {
            From = DateTime.UtcNow.Date.AddDays(-_reporting.DefaultRangeDays),
            To = DateTime.UtcNow.Date.AddDays(1),
        };
        Clients = await _clients.GetAsync(ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        Clients = await _clients.GetAsync(ct);

        if (!ModelState.IsValid)
        {
            return Page();
        }

        await ExecuteAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostExportAsync(CancellationToken ct)
    {
        Clients = await _clients.GetAsync(ct);

        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var composed = BuilderSqlComposer.Compose(Spec, _reporting.MaxExportRows);
            var result = await _runner.RunAsync(
                composed.Sql, composed.Parameters, _reporting.MaxExportRows, ct);

            var stream = new MemoryStream();
            await CsvExporter.WriteAsync(stream, result, ct);
            stream.Position = 0;

            return File(stream, "text/csv", CsvExporter.FileName($"builder-{Spec.GroupBy}"));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Builder CSV export failed.");
            ErrorMessage = ex.Message;
            return Page();
        }
    }

    private async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            var composed = BuilderSqlComposer.Compose(Spec, _reporting.MaxRows);
            GeneratedSql = composed.Sql;
            Result = await _runner.RunAsync(composed.Sql, composed.Parameters, ct: ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Builder query failed.");
            ErrorMessage = ex.Message;
        }
    }
}
