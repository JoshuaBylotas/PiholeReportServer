using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PiholeReportServer.Pages;

[AllowAnonymous]
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class ErrorModel : PageModel
{
    public string? RequestId { get; private set; }

    public void OnGet() =>
        RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
}
