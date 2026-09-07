using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using PiholeReportServer.Configuration;
using PiholeReportServer.Models;
using PiholeReportServer.Services;

namespace PiholeReportServer.Pages.Reports;

public sealed class RunModel : PageModel
{
    private readonly ReportCatalog _catalog;
    private readonly ReportRunner _runner;
    private readonly AiClient _ai;
    private readonly ResultCache _cache;
    private readonly ReportingOptions _reporting;
    private readonly ILogger<RunModel> _log;

    public RunModel(
        ReportCatalog catalog,
        ReportRunner runner,
        AiClient ai,
        ResultCache cache,
        IOptions<ReportingOptions> reporting,
        ILogger<RunModel> log)
    {
        _catalog = catalog;
        _runner = runner;
        _ai = ai;
        _cache = cache;
        _reporting = reporting.Value;
        _log = log;
    }

    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = "";

    public ReportDefinition? Report { get; private set; }

    /// <summary>Parameter name to submitted value, echoed back into the form.</summary>
    public Dictionary<string, string?> Values { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public QueryResult? Result { get; private set; }

    public string? ErrorMessage { get; private set; }

    public bool HasRun { get; private set; }

    public string? ResultToken { get; private set; }

    public int PageSize => _cache.PageSize;

    public AiPanelViewModel AiPanel => AiPanelViewModel.For(
        _ai.Enabled, _ai.Model, User.IsInRole(AppRoles.SqlAuthor), Result,
        Report is null ? "report" : $"the '{Report.Title}' report");

    private bool Load()
    {
        Report = _catalog.Find(Id);
        return Report is not null;
    }

    /// <summary>
    /// Collects each declared parameter from the query string, falling back to
    /// the declared default so the form opens pre-filled.
    /// </summary>
    private void CollectValues(bool useDefaults)
    {
        var now = DateTime.UtcNow;
        foreach (var p in Report!.Parameters)
        {
            var submitted = Request.Query[p.Name].FirstOrDefault()
                            ?? (Request.HasFormContentType ? Request.Form[p.Name].FirstOrDefault() : null);

            Values[p.Name] = !string.IsNullOrWhiteSpace(submitted)
                ? submitted
                : (useDefaults ? ReportCatalog.ResolveDefault(p, now) : submitted);
        }
    }

    public async Task<IActionResult> OnGetAsync(bool run, CancellationToken ct)
    {
        if (!Load())
        {
            return NotFound($"No report with id '{Id}'.");
        }

        CollectValues(useDefaults: true);

        // A report with no required parameters runs straight away; otherwise the
        // user submits the form first.
        var autoRun = run || Report!.Parameters.All(p => !p.Required);
        if (autoRun)
        {
            await ExecuteAsync(ct);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!Load())
        {
            return NotFound($"No report with id '{Id}'.");
        }

        CollectValues(useDefaults: false);
        await ExecuteAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostExportAsync(CancellationToken ct)
    {
        if (!Load())
        {
            return NotFound($"No report with id '{Id}'.");
        }

        CollectValues(useDefaults: false);

        try
        {
            var result = await _runner.RunReportAsync(Report!, Values, _reporting.MaxExportRows, ct);

            var stream = new MemoryStream();
            await CsvExporter.WriteAsync(stream, result, ct);
            stream.Position = 0;

            return File(stream, "text/csv", CsvExporter.FileName(Report!.Id));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "CSV export of report {Id} failed.", Report!.Id);
            ErrorMessage = ex.Message;
            HasRun = true;
            return Page();
        }
    }

    private async Task ExecuteAsync(CancellationToken ct)
    {
        HasRun = true;
        try
        {
            Result = await _runner.RunReportAsync(Report!, Values, ct: ct);
            if (SavedReportStore.OwnerOid(User) is { } owner)
            {
                ResultToken = _cache.Store(owner, Result, Report!.Title);
            }
        }
        catch (ArgumentException ex)
        {
            // Parameter validation - the user can fix this.
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Report {Id} failed.", Report!.Id);
            ErrorMessage = ex.Message;
        }
    }
}
