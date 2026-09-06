using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PiholeReportServer.Services;

namespace PiholeReportServer.Pages.Ai;

/// <summary>
/// Narrates a digest of the result the user is already looking at, so it needs no more
/// privilege than viewing that result — the site-wide Viewer fallback policy is enough.
/// </summary>
public sealed class ExplainModel : PageModel
{
    private readonly AiClient _ai;
    private readonly ILogger<ExplainModel> _log;

    public ExplainModel(AiClient ai, ILogger<ExplainModel> log)
    {
        _ai = ai;
        _log = log;
    }

    public sealed class ExplainInput
    {
        public string? Digest { get; set; }
    }

    public async Task<IActionResult> OnPostAsync([FromBody] ExplainInput request, CancellationToken ct)
    {
        if (!_ai.Enabled)
        {
            return new JsonResult(new { ok = false, error = "The AI assistant is not configured." });
        }

        var digest = request?.Digest;
        if (string.IsNullOrWhiteSpace(digest))
        {
            return new JsonResult(new { ok = false, error = "Run a query first — there is nothing to explain yet." });
        }
        if (digest.Length > 20_000)
        {
            return new JsonResult(new { ok = false, error = "That summary is unexpectedly large." });
        }

        try
        {
            var text = await _ai.SummariseFromDigestAsync(digest, ct);
            return new JsonResult(new { ok = true, summary = text, model = _ai.Model });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AI summary failed.");
            return new JsonResult(new { ok = false, error = ex.Message });
        }
    }
}
