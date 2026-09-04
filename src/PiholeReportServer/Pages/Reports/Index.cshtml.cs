using Microsoft.AspNetCore.Mvc.RazorPages;
using PiholeReportServer.Models;
using PiholeReportServer.Services;

namespace PiholeReportServer.Pages.Reports;

public sealed class IndexModel : PageModel
{
    private readonly ReportCatalog _catalog;
    private readonly SavedReportStore _saved;
    private readonly ILogger<IndexModel> _log;

    public IndexModel(ReportCatalog catalog, SavedReportStore saved, ILogger<IndexModel> log)
    {
        _catalog = catalog;
        _saved = saved;
        _log = log;
    }

    public IReadOnlyList<IGrouping<string, ReportDefinition>> Categories { get; private set; } = [];

    /// <summary>The signed-in user's own saved reports, both builder and SQL.</summary>
    public IReadOnlyList<SavedReport> MySaved { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Categories = _catalog.ByCategory().ToList();

        if (SavedReportStore.OwnerOid(User) is { } owner)
        {
            try
            {
                MySaved = await _saved.ListAsync(owner, kind: null, ct);
            }
            catch (Exception ex)
            {
                // The pre-canned library must still render if saved reports fail.
                _log.LogWarning(ex, "Saved reports unavailable on the report index.");
            }
        }
    }
}
