using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PiholeReportServer.Services;

namespace PiholeReportServer.Pages.Results;

/// <summary>
/// Serves one page of a cached result set.
/// <para>
/// Named with a trailing underscore because <c>PageModel</c> is the base class this
/// derives from, and a page called "Page" would otherwise collide with it.
/// </para>
/// <para>
/// Inherits the site-wide Viewer requirement: a cached result belongs to the person
/// who ran it, and <see cref="ResultCache"/> checks the owner on every read, so a
/// leaked token is useless to anyone else.
/// </para>
/// </summary>
public sealed class PageModel_ : PageModel
{
    private readonly ResultCache _cache;

    public PageModel_(ResultCache cache) => _cache = cache;

    public IActionResult OnGet(string? rs, int p = 0, string? q = null)
    {
        if (string.IsNullOrWhiteSpace(rs))
        {
            return new JsonResult(new { ok = false, error = "No result set specified." });
        }

        var owner = SavedReportStore.OwnerOid(User);
        if (owner is null)
        {
            return new JsonResult(new { ok = false, error = "Could not identify the signed-in user." });
        }

        // Bound the search term: it is scanned against every cached row, so an
        // unbounded string is wasted work.
        if (q is { Length: > 200 })
        {
            q = q[..200];
        }

        var page = _cache.GetPage(owner, rs, p, q);
        if (page is null)
        {
            // Expired and "not yours" are reported identically on purpose: telling
            // them apart would reveal whether a token exists.
            return new JsonResult(new
            {
                ok = false,
                expired = true,
                error = "That result is no longer available — run the query again.",
            });
        }

        return new JsonResult(new
        {
            ok = true,
            page.Page,
            page.PageCount,
            page.PageSize,
            page.MatchingRows,
            page.TotalRows,
            page.Truncated,
            page.RowCap,
            page.IsFiltered,
            first = page.FirstRowNumber,
            last = page.LastRowNumber,
            columns = page.Columns.Select(c => new { c.Name, numeric = IsNumeric(c.ClrType) }),
            rows = page.Rows.Select(r => r.Select(Format).ToArray()),
        });
    }

    private static readonly HashSet<string> NumericTypes =
    [
        "Int32", "Int64", "Int16", "Byte", "Decimal", "Double", "Single"
    ];

    private static bool IsNumeric(string clrType) => NumericTypes.Contains(clrType);

    /// <summary>
    /// Values are formatted server-side so the browser never has to guess a type,
    /// and so number grouping and timestamps match the CSV export exactly.
    /// </summary>
    private static string? Format(object? v) => v switch
    {
        null => null,
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
        long or int or short => Convert.ToInt64(v).ToString("N0"),
        _ => v.ToString(),
    };
}
