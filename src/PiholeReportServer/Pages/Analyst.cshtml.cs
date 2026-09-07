using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using PiholeReportServer.Configuration;
using PiholeReportServer.Models;
using PiholeReportServer.Services;

namespace PiholeReportServer.Pages;

/// <summary>
/// The analysis agent.
/// <para>
/// Carries the SqlAuthor role requirement: the agent writes and runs its own queries,
/// which is the same capability as the console, reached by a different route. Gating
/// it any lower would let a viewer run arbitrary SQL by asking nicely.
/// </para>
/// </summary>
[Authorize(Policy = AuthorizationPolicies.SqlAuthor)]
public sealed class AnalystModel : PageModel
{
    private readonly AiAgent _agent;
    private readonly AiOptions _opt;
    private readonly AiEndpointSelector _hosts;

    public AnalystModel(AiAgent agent, IOptions<AiOptions> opt, AiEndpointSelector hosts)
    {
        _agent = agent;
        _opt = opt.Value;
        _hosts = hosts;
    }

    [BindProperty]
    [System.ComponentModel.DataAnnotations.StringLength(500)]
    public string? Question { get; set; }

    public AgentRun? Run { get; private set; }

    public bool Enabled => _agent.Enabled;

    public int MaxSteps => _agent.MaxSteps;

    public int BudgetSeconds => _opt.AgentBudgetSeconds;

    /// <summary>
    /// The model actually in use. This is not <c>Ai:Model</c>: when the preferred host
    /// is away the standby serves a smaller model, and the page would otherwise name
    /// one that is not answering.
    /// </summary>
    public string Model => _hosts.Current.Model;

    /// <summary>True while the preferred host is unreachable and the standby is serving.</summary>
    public bool UsingFallback => _hosts.Current.IsFallback;

    /// <summary>Host serving requests right now.</summary>
    public string ActiveEndpoint => _hosts.Current.Endpoint;

    /// <summary>Starting points, so the page is not a blank box.</summary>
    public static readonly string[] Examples =
    [
        "Which device generates the most advertising and tracking traffic this week?",
        "What kind of traffic is my Samsung TV making, by category?",
        "Has any device started querying something new and unusual in the last 3 days?",
        "Which blocklist would have caught the most traffic, and what does it block?",
        "Compare weekday and weekend traffic by category.",
        "Are any devices reaching domains categorised as malware or phishing?",
    ];

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!Enabled)
        {
            ModelState.AddModelError(string.Empty, "The AI assistant is not configured.");
            return Page();
        }
        if (string.IsNullOrWhiteSpace(Question))
        {
            ModelState.AddModelError(nameof(Question), "Ask a question first.");
            return Page();
        }

        Run = await _agent.RunAsync(Question.Trim(), ct);
        return Page();
    }
}
