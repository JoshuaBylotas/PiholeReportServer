using Microsoft.Extensions.Options;
using PiholeReportServer.Configuration;

namespace PiholeReportServer.Services;

/// <summary>One inference host, and the model to ask for on it.</summary>
public sealed record AiTarget
{
    public AiTarget(string endpoint, string model, bool isFallback)
    {
        Endpoint = endpoint.TrimEnd('/');
        Model = model;
        IsFallback = isFallback;
        BaseUri = new Uri(Endpoint + "/");
    }

    public string Endpoint { get; }
    public string Model { get; }

    /// <summary>True for the standby host, so callers can say which one answered.</summary>
    public bool IsFallback { get; }

    private Uri BaseUri { get; }

    /// <summary>
    /// Absolute URI for a relative API path. The client cannot use
    /// <c>HttpClient.BaseAddress</c> any more: which host a request goes to is now
    /// decided per request, and BaseAddress is shared mutable state on a client that
    /// the factory pools across requests.
    /// </summary>
    public Uri Uri(string path) => new(BaseUri, path);

    public override string ToString() => $"{Endpoint} ({Model})";
}

/// <summary>
/// Chooses which inference host to use, and remembers that the preferred one is
/// unreachable so the next few requests do not each pay its connect timeout.
/// <para>
/// This exists because the fast host is a laptop that leaves the building. Without a
/// standby the AI features and the 03:00 job simply stop producing whenever it is
/// away; the slower CPU host is always present, so it is worth having as a floor.
/// </para>
/// <para>
/// Registered as a singleton: the whole point is that the "primary is away" state
/// outlives one request.
/// </para>
/// </summary>
public sealed class AiEndpointSelector
{
    private readonly TimeProvider _clock;
    private readonly ILogger<AiEndpointSelector> _log;
    private readonly TimeSpan _cooldown;

    /// <summary>UTC ticks until which the primary is presumed away; 0 means healthy.</summary>
    private long _primaryDownUntil;

    public AiEndpointSelector(
        IOptions<AiOptions> options,
        ILogger<AiEndpointSelector> log,
        TimeProvider? clock = null)
    {
        var opt = options.Value;
        _log = log;
        _clock = clock ?? TimeProvider.System;
        _cooldown = TimeSpan.FromSeconds(Math.Clamp(opt.FallbackRetryPrimarySeconds, 15, 3600));

        Primary = new AiTarget(
            string.IsNullOrWhiteSpace(opt.Endpoint) ? "" : opt.Endpoint,
            opt.Model,
            isFallback: false);

        // A fallback pointing at the same host is not a fallback; it would just
        // double the wait when that host is down.
        if (!string.IsNullOrWhiteSpace(opt.FallbackEndpoint) &&
            !string.Equals(opt.FallbackEndpoint.TrimEnd('/'), Primary.Endpoint,
                           StringComparison.OrdinalIgnoreCase))
        {
            Fallback = new AiTarget(
                opt.FallbackEndpoint,
                string.IsNullOrWhiteSpace(opt.FallbackModel) ? opt.Model : opt.FallbackModel,
                isFallback: true);
        }
    }

    public AiTarget Primary { get; }

    public AiTarget? Fallback { get; }

    public bool HasFallback => Fallback is not null;

    /// <summary>The host a new request should go to.</summary>
    public AiTarget Current
    {
        get
        {
            if (Fallback is null)
            {
                return Primary;
            }

            var until = Interlocked.Read(ref _primaryDownUntil);
            if (until == 0)
            {
                return Primary;
            }

            if (_clock.GetUtcNow().UtcTicks >= until)
            {
                // The cooldown has elapsed, so let one request try the primary again.
                // Clearing it here rather than waiting for a success means a still-absent
                // host costs one connect attempt per interval instead of one per request.
                Interlocked.CompareExchange(ref _primaryDownUntil, 0, until);
                return Primary;
            }

            return Fallback;
        }
    }

    /// <summary>
    /// Hosts to try, in order, for one logical request: the current choice first, then
    /// the other one if there is one. Both directions matter — when the primary is in
    /// cooldown the fallback leads, and if the fallback is also down it is worth
    /// discovering that the primary came back rather than just failing.
    /// </summary>
    public IReadOnlyList<AiTarget> Attempts()
    {
        var first = Current;
        if (Fallback is null)
        {
            return [first];
        }
        var other = ReferenceEquals(first, Primary) ? Fallback : Primary;
        return [first, other];
    }

    /// <summary>Records that a host answered, ending any cooldown on the primary.</summary>
    public void ReportReachable(AiTarget target)
    {
        if (target.IsFallback)
        {
            return;
        }

        if (Interlocked.Exchange(ref _primaryDownUntil, 0) != 0)
        {
            _log.LogInformation("AI: primary host {Endpoint} is reachable again.", target.Endpoint);
        }
    }

    /// <summary>
    /// Records that a host could not be reached at all. Only the primary has a
    /// cooldown: there is nothing to fall back to from the fallback.
    /// </summary>
    public void ReportUnreachable(AiTarget target)
    {
        if (target.IsFallback || Fallback is null)
        {
            return;
        }

        var until = _clock.GetUtcNow().Add(_cooldown).UtcTicks;
        if (Interlocked.Exchange(ref _primaryDownUntil, until) == 0)
        {
            _log.LogWarning(
                "AI: primary host {Primary} is unreachable; using {Fallback} for the next {Seconds}s.",
                target.Endpoint, Fallback.Endpoint, (int)_cooldown.TotalSeconds);
        }
    }

    /// <summary>How much of the cooldown is left, or null when the primary is in use.</summary>
    public TimeSpan? PrimaryCooldownRemaining
    {
        get
        {
            var until = Interlocked.Read(ref _primaryDownUntil);
            if (until == 0)
            {
                return null;
            }
            var left = new DateTimeOffset(until, TimeSpan.Zero) - _clock.GetUtcNow();
            return left > TimeSpan.Zero ? left : null;
        }
    }
}
