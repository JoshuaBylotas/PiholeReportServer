using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PiholeReportServer.Configuration;
using PiholeReportServer.Models;
using PiholeReportServer.Services;

namespace PiholeReportServer.Pages.Devices;

/// <summary>
/// The device editor: state what each device is called, and push the result to
/// AD DNS.
/// <para>
/// Reading is open to any viewer - the table is the answer to "what is
/// 10.20.0.152", and that is a reporting question. Writing requires
/// <see cref="AppRoles.DeviceEditor"/>, because it changes what the network's
/// name resolution says.
/// </para>
/// </summary>
public sealed class IndexModel : PageModel
{
    private readonly DeviceTruthStore _devices;
    private readonly DnsPushStore _push;
    private readonly IConfiguration _config;
    private readonly ILogger<IndexModel> _log;

    public IndexModel(
        DeviceTruthStore devices,
        DnsPushStore push,
        IConfiguration config,
        ILogger<IndexModel> log)
    {
        _devices = devices;
        _push = push;
        _config = config;
        _log = log;
    }

    public IReadOnlyList<DeviceRow> Rows { get; private set; } = [];

    public IReadOnlyDictionary<string, List<DeviceCandidate>> Candidates { get; private set; } =
        new Dictionary<string, List<DeviceCandidate>>();

    public IReadOnlyList<DnsPushRequestRow> RecentPushes { get; private set; } = [];

    /// <summary>The log of the most recent finished push, so the button shows its effect.</summary>
    public IReadOnlyList<DnsPushLogRow> LastPushLog { get; private set; } = [];

    /// <summary>Show only the devices with no name worth trusting.</summary>
    [BindProperty(SupportsGet = true, Name = "review")]
    public bool OnlyReview { get; set; }

    /// <summary>Which request's log to expand, if any.</summary>
    [BindProperty(SupportsGet = true, Name = "log")]
    public int? ShowLogFor { get; set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    /// <summary>
    /// Whether this user may state names and push.
    /// <para>
    /// When app roles are not being required at all - a fresh install, per
    /// docs/02-entra-id-setup.md - any authenticated user may edit, matching how
    /// the Viewer policy falls back. Once RequireAppRoles is on, the role is
    /// needed.
    /// </para>
    /// </summary>
    public bool CanEdit =>
        !_config.GetValue("AzureAd:RequireAppRoles", false)
        || User.IsInRole(AppRoles.DeviceEditor);

    public int NeedsReviewCount { get; private set; }
    public int StatedCount { get; private set; }
    public int DisagreeCount { get; private set; }

    public async Task OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    private async Task LoadAsync(CancellationToken ct)
    {
        Rows = await _devices.ListAsync(OnlyReview, ct);
        Candidates = await _devices.CandidatesAsync(ct);

        // Counted over the full set, not the filtered view, or turning the
        // filter on would make the numbers describing it change too.
        var all = OnlyReview ? await _devices.ListAsync(false, ct) : Rows;
        NeedsReviewCount = all.Count(r => r.NeedsReview);
        StatedCount = all.Count(r => r.IsStated);
        DisagreeCount = all.Count(r => r.AdDnsAgrees == false);

        try
        {
            RecentPushes = await _push.RecentAsync(8, ct);
            var forLog = ShowLogFor ?? RecentPushes.FirstOrDefault(p => p.IsFinished)?.Id;
            if (forLog is { } id)
            {
                LastPushLog = await _push.LogAsync(id, ct);
                ShowLogFor = id;
            }
        }
        catch (Exception ex)
        {
            // The editor is still usable when the push history is not.
            _log.LogWarning(ex, "DNS push history unavailable on the devices page.");
        }
    }

    public async Task<IActionResult> OnPostSaveAsync(
        string mac, string? name, string? notes, bool publishDns, CancellationToken ct)
    {
        if (!CanEdit)
        {
            return Forbid();
        }

        if (DeviceTruthStore.NormaliseMac(mac) is null)
        {
            ErrorMessage = $"Not a MAC address: {mac}";
            return RedirectToPage(new { review = OnlyReview });
        }

        if (DeviceTruthStore.ValidateName(name) is { } problem)
        {
            ErrorMessage = $"{mac}: {problem}";
            return RedirectToPage(new { review = OnlyReview });
        }

        try
        {
            await _devices.SetAsync(mac, name!.Trim(), notes, publishDns, User, ct);
            StatusMessage = $"{mac} is now {name!.Trim()}.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to state a name for {Mac}", mac);
            ErrorMessage = $"Could not save {mac}: {ex.Message}";
        }

        return RedirectToPage(new { review = OnlyReview });
    }

    public async Task<IActionResult> OnPostClearAsync(string mac, CancellationToken ct)
    {
        if (!CanEdit)
        {
            return Forbid();
        }

        try
        {
            await _devices.ClearAsync(mac, User, ct);
            StatusMessage = $"Withdrew the stated name for {mac}; it falls back to the sources again.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to withdraw the name for {Mac}", mac);
            ErrorMessage = $"Could not clear {mac}: {ex.Message}";
        }

        return RedirectToPage(new { review = OnlyReview });
    }

    /// <summary>
    /// Queues a push. Does not perform one - the agent on WINAD02 does, which is
    /// why the page reports "queued" rather than "done".
    /// </summary>
    public async Task<IActionResult> OnPostPushAsync(
        string? mac, bool dryRun, CancellationToken ct)
    {
        if (!CanEdit)
        {
            return Forbid();
        }

        try
        {
            var id = await _push.QueueAsync(mac, dryRun, User, ct);
            StatusMessage = id is null
                ? "A push is already waiting for the agent. Give it a moment."
                : $"Queued {(dryRun ? "a dry run" : "a push")} as request #{id}"
                  + $"{(mac is null ? " for every publishable device" : $" for {mac}")}."
                  + " The agent on WINAD02 picks it up within a minute.";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to queue a DNS push");
            ErrorMessage = $"Could not queue the push: {ex.Message}";
        }

        return RedirectToPage(new { review = OnlyReview });
    }
}
