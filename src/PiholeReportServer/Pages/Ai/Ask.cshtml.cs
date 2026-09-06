using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PiholeReportServer.Configuration;
using PiholeReportServer.Services;

namespace PiholeReportServer.Pages.Ai;

/// <summary>
/// Natural language to SQL.
/// <para>
/// A page of its own, rather than a handler beside <see cref="ExplainModel"/>, because
/// Razor Pages only honours <c>[Authorize]</c> at page level — and these two need
/// different privileges. Generating SQL is arbitrary querying with extra steps, so it
/// carries the same role as writing it by hand.
/// </para>
/// <para>
/// This endpoint only ever <em>returns</em> SQL. Running it is a separate, deliberate
/// step by the user, and the console screens it again at that point.
/// </para>
/// </summary>
[Authorize(Policy = AuthorizationPolicies.SqlAuthor)]
public sealed class AskModel : PageModel
{
    private readonly AiClient _ai;
    private readonly ILogger<AskModel> _log;

    public AskModel(AiClient ai, ILogger<AskModel> log)
    {
        _ai = ai;
        _log = log;
    }

    public sealed class AskInput
    {
        public string? Question { get; set; }
    }

    public async Task<IActionResult> OnPostAsync([FromBody] AskInput request, CancellationToken ct)
    {
        if (!_ai.Enabled)
        {
            return new JsonResult(new { ok = false, error = "The AI assistant is not configured." });
        }

        var question = request?.Question?.Trim();
        if (string.IsNullOrWhiteSpace(question))
        {
            return new JsonResult(new { ok = false, error = "Ask a question first." });
        }
        if (question.Length > 500)
        {
            return new JsonResult(new { ok = false, error = "That question is too long (500 characters maximum)." });
        }

        try
        {
            var suggestion = await _ai.SuggestSqlAsync(question, ct);

            // Screen the completion with the same guard the console uses, and report
            // the verdict rather than hiding it: seeing why a suggestion was refused
            // is more useful than a blank failure.
            var verdict = SqlGuard.Validate(suggestion.Sql);

            _log.LogInformation("AI question from {User}: guard allowed={Allowed}",
                User.Identity?.Name, verdict.Allowed);

            return new JsonResult(new
            {
                ok = true,
                sql = suggestion.Sql,
                notes = suggestion.Notes,
                allowed = verdict.Allowed,
                reason = verdict.Reason,
                model = _ai.Model,
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AI question failed.");
            return new JsonResult(new { ok = false, error = ex.Message });
        }
    }
}
