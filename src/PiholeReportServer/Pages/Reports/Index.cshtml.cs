using Microsoft.AspNetCore.Mvc.RazorPages;
using PiholeReportServer.Models;
using PiholeReportServer.Services;

namespace PiholeReportServer.Pages.Reports;

public sealed class IndexModel : PageModel
{
    private readonly ReportCatalog _catalog;

    public IndexModel(ReportCatalog catalog) => _catalog = catalog;

    public IReadOnlyList<IGrouping<string, ReportDefinition>> Categories { get; private set; } = [];

    public void OnGet() => Categories = _catalog.ByCategory().ToList();
}
