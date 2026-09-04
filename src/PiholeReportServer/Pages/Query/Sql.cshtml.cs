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
    private readonly SavedReportStore _saved;
    private readonly ReportingOptions _reporting;
    private readonly ILogger<SqlModel> _log;

    public SqlModel(
        ReportRunner runner,
        SavedReportStore saved,
        IOptions<ReportingOptions> reporting,
        ILogger<SqlModel> log)
    {
        _runner = runner;
        _saved = saved;
        _reporting = reporting.Value;
        _log = log;
    }

    [BindProperty]
    public string? Sql { get; set; }

    [BindProperty]
    public SaveReportInput SaveInput { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public int? SavedId { get; set; }

    public IReadOnlyList<SavedReport> MySaved { get; private set; } = [];

    public string? StatusMessage { get; private set; }

    private string? Owner => SavedReportStore.OwnerOid(User);

    public QueryResult? Result { get; private set; }

    public string? ErrorMessage { get; private set; }

    public bool Rejected { get; private set; }

    public int MaxRows => _reporting.MaxRows;

    public bool Enabled => _reporting.EnableRawSql;

    private const string Sample = """
        -- Busiest clients over the last 24 hours, resolved to host names
        SELECT TOP (50)
               COALESCE(dc.name, q.client) AS client,
               COUNT_BIG(*)                    AS queries,
               COUNT(DISTINCT q.domain)        AS distinct_domains
        FROM dbo.PiholeQueries AS q
             LEFT JOIN dbo.DimClient AS dc ON dc.ip = q.client
        WHERE q.ts >= DATEADD(hour, -24, SYSUTCDATETIME())
        GROUP BY COALESCE(dc.name, q.client)
        ORDER BY queries DESC
        """;

    private async Task LoadSavedAsync(CancellationToken ct)
    {
        if (Owner is not { } owner)
        {
            return;
        }

        try
        {
            MySaved = await _saved.ListAsync(owner, SavedReportKind.Sql, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Saved queries unavailable.");
        }
    }

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (TempData["Status"] is string carried)
        {
            StatusMessage = carried;
        }

        await LoadSavedAsync(ct);

        if (SavedId is { } id && Owner is { } owner)
        {
            var report = await _saved.GetAsync(owner, id, ct);
            if (report is null)
            {
                ErrorMessage = "That saved query does not exist, or is not yours.";
                return;
            }

            Sql = report.Payload;
            SaveInput = new SaveReportInput { Name = report.Name, Description = report.Description };
            StatusMessage = $"Loaded '{report.Name}'. Review it, then run.";
            await _saved.TouchAsync(owner, id, ct);
            return;
        }

        Sql ??= Sample;
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        await LoadSavedAsync(ct);

        if (Owner is not { } owner)
        {
            ErrorMessage = "The signed-in user could not be identified, so nothing was saved.";
            return Page();
        }

        if (string.IsNullOrWhiteSpace(SaveInput.Name))
        {
            ErrorMessage = "Give the query a name before saving it.";
            return Page();
        }

        // Refuse to store something that would be rejected at run time anyway.
        var verdict = SqlGuard.Validate(Sql);
        if (!verdict.Allowed)
        {
            Rejected = true;
            ErrorMessage = $"Not saved — {verdict.Reason}";
            return Page();
        }

        try
        {
            var id = await _saved.SaveAsync(
                owner,
                SavedReportStore.OwnerName(User),
                SaveInput.Name!,
                SaveInput.Description,
                SavedReportKind.Sql,
                Sql!,
                ct);

            MySaved = await _saved.ListAsync(owner, SavedReportKind.Sql, ct);
            SavedId = id;
            StatusMessage = $"Saved '{SaveInput.Name!.Trim()}'.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not save: {ex.Message}";
        }

        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id, CancellationToken ct)
    {
        if (Owner is not { } owner)
        {
            return Forbid();
        }

        var report = await _saved.GetAsync(owner, id, ct);
        var removed = await _saved.DeleteAsync(owner, id, ct);

        TempData["Status"] = removed
            ? $"Deleted '{report?.Name ?? id.ToString()}'."
            : "That saved query does not exist, or is not yours.";

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        await LoadSavedAsync(ct);

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
        await LoadSavedAsync(ct);

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
