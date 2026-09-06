using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using PiholeReportServer.Configuration;
using PiholeReportServer.Models;
using PiholeReportServer.Services;

namespace PiholeReportServer.Pages.Query;

public sealed class BuilderModel : PageModel
{
    /// <summary>
    /// Enums are written as names, not ordinals, so a saved report survives someone
    /// inserting a new dimension into the middle of the enum — an ordinal would
    /// silently re-point at a different column.
    /// </summary>
    internal static readonly JsonSerializerOptions SpecJson = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    private readonly ReportRunner _runner;
    private readonly ClientDirectory _clients;
    private readonly AdlistDirectory _adlists;
    private readonly SavedReportStore _saved;
    private readonly AiClient _ai;
    private readonly ReportingOptions _reporting;
    private readonly ILogger<BuilderModel> _log;

    public BuilderModel(
        ReportRunner runner,
        ClientDirectory clients,
        AdlistDirectory adlists,
        SavedReportStore saved,
        AiClient ai,
        IOptions<ReportingOptions> reporting,
        ILogger<BuilderModel> log)
    {
        _runner = runner;
        _clients = clients;
        _adlists = adlists;
        _saved = saved;
        _ai = ai;
        _reporting = reporting.Value;
        _log = log;
    }

    [BindProperty]
    public BuilderSpec Spec { get; set; } = new();

    [BindProperty]
    public SaveReportInput SaveInput { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public int? SavedId { get; set; }

    public QueryResult? Result { get; private set; }

    public string? GeneratedSql { get; private set; }

    public string? ErrorMessage { get; private set; }

    public string? StatusMessage { get; private set; }

    public ClientLookup ClientList { get; private set; } = new([], null);

    public AdlistLookup AdlistList { get; private set; } = new([], null);

    public IReadOnlyList<SavedReport> MySaved { get; private set; } = [];

    public AiPanelViewModel AiPanel => AiPanelViewModel.For(
        _ai.Enabled, _ai.Model, User.IsInRole(AppRoles.SqlAuthor), Result,
        $"report builder, grouped by {Spec.GroupBy}, metric {Spec.Metric}");

    private string? Owner => SavedReportStore.OwnerOid(User);

    private async Task LoadLookupsAsync(CancellationToken ct)
    {
        ClientList = await _clients.GetAsync(ct);
        AdlistList = await _adlists.GetAsync(ct);

        if (Owner is { } owner)
        {
            try
            {
                MySaved = await _saved.ListAsync(owner, SavedReportKind.Builder, ct);
            }
            catch (Exception ex)
            {
                // A saved-report problem must not take the builder itself down.
                _log.LogWarning(ex, "Saved reports unavailable.");
            }
        }
    }

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (TempData["Status"] is string carried)
        {
            StatusMessage = carried;
        }

        Spec = new BuilderSpec
        {
            From = DateTime.UtcNow.Date.AddDays(-_reporting.DefaultRangeDays),
            To = DateTime.UtcNow.Date.AddDays(1),
        };

        await LoadLookupsAsync(ct);

        // ?savedId=N loads that report's definition and runs it.
        if (SavedId is { } id && Owner is { } owner)
        {
            var report = await _saved.GetAsync(owner, id, ct);
            if (report is null)
            {
                ErrorMessage = "That saved report does not exist, or is not yours.";
                return;
            }

            var restored = Deserialise(report.Payload);
            if (restored is null)
            {
                ErrorMessage = $"'{report.Name}' could not be read — its stored definition is not valid.";
                return;
            }

            Spec = restored;
            SaveInput = new SaveReportInput { Name = report.Name, Description = report.Description };
            StatusMessage = $"Loaded '{report.Name}'.";

            await ExecuteAsync(ct);
            await _saved.TouchAsync(owner, id, ct);
        }
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        await LoadLookupsAsync(ct);

        // The save form's validation must not block simply running a query.
        ModelState.Remove($"{nameof(SaveInput)}.{nameof(SaveReportInput.Name)}");

        await ExecuteAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        await LoadLookupsAsync(ct);

        if (Owner is not { } owner)
        {
            ErrorMessage = "The signed-in user could not be identified, so nothing was saved.";
            return Page();
        }

        if (string.IsNullOrWhiteSpace(SaveInput.Name))
        {
            ErrorMessage = "Give the report a name before saving it.";
            await ExecuteAsync(ct);
            return Page();
        }

        try
        {
            var id = await _saved.SaveAsync(
                owner,
                SavedReportStore.OwnerName(User),
                SaveInput.Name!,
                SaveInput.Description,
                SavedReportKind.Builder,
                JsonSerializer.Serialize(Spec, SpecJson),
                ct);

            MySaved = await _saved.ListAsync(owner, SavedReportKind.Builder, ct);
            SavedId = id;
            StatusMessage = $"Saved '{SaveInput.Name!.Trim()}'.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not save: {ex.Message}";
        }

        await ExecuteAsync(ct);
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
            : "That saved report does not exist, or is not yours.";

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostExportAsync(CancellationToken ct)
    {
        await LoadLookupsAsync(ct);

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

    internal static BuilderSpec? Deserialise(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<BuilderSpec>(payload, SpecJson);
        }
        catch (JsonException)
        {
            return null;
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
