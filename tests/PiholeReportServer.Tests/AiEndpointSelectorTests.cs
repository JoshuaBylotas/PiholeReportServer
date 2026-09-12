using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiholeReportServer.Configuration;
using PiholeReportServer.Services;
using Xunit;

namespace PiholeReportServer.Tests;

/// <summary>
/// The standby host exists because the fast one is a laptop that leaves the
/// building. These tests pin the behaviour that makes that useful: it switches
/// without being told, it stops paying the connect timeout while the fast host is
/// away, and it comes back on its own.
/// </summary>
public class AiEndpointSelectorTests
{
    /// <summary>
    /// A controllable clock. Hand-rolled rather than taking a dependency on
    /// Microsoft.Extensions.TimeProvider.Testing for one type.
    /// </summary>
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private const string Fast = "http://10.20.0.139:11434";
    private const string Slow = "http://10.20.0.14:11434";

    private static (AiEndpointSelector Sel, Clock Clock) Build(
        string endpoint = Fast,
        string fallback = Slow,
        string model = "qwen2.5:14b-instruct",
        string fallbackModel = "qwen2.5:7b-instruct",
        int cooldown = 120)
    {
        var clock = new Clock(new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));
        var opt = Options.Create(new AiOptions
        {
            Enabled = true,
            Endpoint = endpoint,
            Model = model,
            FallbackEndpoint = fallback,
            FallbackModel = fallbackModel,
            FallbackRetryPrimarySeconds = cooldown,
        });
        return (new AiEndpointSelector(opt, NullLogger<AiEndpointSelector>.Instance, clock), clock);
    }

    [Fact]
    public void Prefers_the_primary_while_it_is_healthy()
    {
        var (sel, _) = Build();
        Assert.Equal(Fast, sel.Current.Endpoint);
        Assert.False(sel.Current.IsFallback);
        Assert.Null(sel.PrimaryCooldownRemaining);
    }

    [Fact]
    public void Switches_to_the_standby_once_the_primary_is_unreachable()
    {
        var (sel, _) = Build();
        sel.ReportUnreachable(sel.Primary);

        Assert.Equal(Slow, sel.Current.Endpoint);
        Assert.True(sel.Current.IsFallback);
        // The model changes with the host: the standby is the lesser machine and
        // does not have the 14B installed.
        Assert.Equal("qwen2.5:7b-instruct", sel.Current.Model);
    }

    [Fact]
    public void Stays_on_the_standby_for_the_whole_cooldown()
    {
        // This is the point of the cooldown. Without it every request would spend the
        // connect timeout rediscovering that the laptop is still away.
        var (sel, clock) = Build(cooldown: 120);
        sel.ReportUnreachable(sel.Primary);

        clock.Advance(TimeSpan.FromSeconds(119));
        Assert.Equal(Slow, sel.Current.Endpoint);
        Assert.NotNull(sel.PrimaryCooldownRemaining);
    }

    [Fact]
    public void Retries_the_primary_once_the_cooldown_expires()
    {
        var (sel, clock) = Build(cooldown: 120);
        sel.ReportUnreachable(sel.Primary);

        clock.Advance(TimeSpan.FromSeconds(121));

        // Recovery is automatic. Nothing has told it the laptop is back - it simply
        // tries again, which is what makes this need no operator action.
        Assert.Equal(Fast, sel.Current.Endpoint);
    }

    [Fact]
    public void A_failed_retry_starts_a_fresh_cooldown_rather_than_retrying_every_request()
    {
        var (sel, clock) = Build(cooldown: 120);
        sel.ReportUnreachable(sel.Primary);
        clock.Advance(TimeSpan.FromSeconds(121));

        Assert.Equal(Fast, sel.Current.Endpoint);   // the one probe request
        sel.ReportUnreachable(sel.Primary);          // which also fails

        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(Slow, sel.Current.Endpoint);    // back to the standby, not probing again
    }

    [Fact]
    public void A_success_on_the_primary_clears_the_cooldown_immediately()
    {
        var (sel, _) = Build();
        sel.ReportUnreachable(sel.Primary);
        Assert.True(sel.Current.IsFallback);

        sel.ReportReachable(sel.Primary);

        Assert.Equal(Fast, sel.Current.Endpoint);
        Assert.Null(sel.PrimaryCooldownRemaining);
    }

    [Fact]
    public void A_standby_failure_does_not_divert_traffic_away_from_the_primary()
    {
        // There is nothing to fall back to from the fallback, and a cooldown here
        // would push requests onto a host that was never reported broken.
        var (sel, _) = Build();
        sel.ReportUnreachable(sel.Fallback!);

        Assert.Equal(Fast, sel.Current.Endpoint);
        Assert.Null(sel.PrimaryCooldownRemaining);
    }

    [Fact]
    public void Attempts_lists_both_hosts_so_one_request_can_try_each()
    {
        var (sel, _) = Build();

        var healthy = sel.Attempts();
        Assert.Equal([Fast, Slow], healthy.Select(t => t.Endpoint));

        sel.ReportUnreachable(sel.Primary);

        // Order reverses, so a request during the cooldown still gets a second chance
        // at the primary if the standby is down too.
        var degraded = sel.Attempts();
        Assert.Equal([Slow, Fast], degraded.Select(t => t.Endpoint));
    }

    [Fact]
    public void With_no_fallback_configured_there_is_nothing_to_switch_to()
    {
        var (sel, _) = Build(fallback: "");

        Assert.False(sel.HasFallback);
        sel.ReportUnreachable(sel.Primary);

        // No cooldown either: diverting to nowhere would only add a wait.
        Assert.Equal(Fast, sel.Current.Endpoint);
        Assert.Single(sel.Attempts());
        Assert.Null(sel.PrimaryCooldownRemaining);
    }

    [Fact]
    public void A_fallback_pointing_at_the_primary_is_ignored()
    {
        // Otherwise an outage would be waited out twice for no benefit.
        var (sel, _) = Build(fallback: Fast + "/");
        Assert.False(sel.HasFallback);
    }

    [Fact]
    public void The_standby_inherits_the_primary_model_when_none_is_named()
    {
        var (sel, _) = Build(fallbackModel: "");
        Assert.Equal("qwen2.5:14b-instruct", sel.Fallback!.Model);
    }

    [Theory]
    [InlineData(1, 15)]        // clamped up: a 1s cooldown defeats the purpose
    [InlineData(120, 120)]
    [InlineData(99999, 3600)]  // clamped down: an hour is long enough to be stale
    public void The_cooldown_is_clamped_to_a_sane_range(int configured, int expected)
    {
        var (sel, clock) = Build(cooldown: configured);
        sel.ReportUnreachable(sel.Primary);

        clock.Advance(TimeSpan.FromSeconds(expected - 1));
        Assert.True(sel.Current.IsFallback, $"should still be on the standby at {expected - 1}s");

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(sel.Current.IsFallback, $"should retry the primary after {expected}s");
    }

    [Fact]
    public void A_trailing_slash_does_not_produce_a_double_slash_in_request_uris()
    {
        var (sel, _) = Build(endpoint: Fast + "/");
        Assert.Equal($"{Fast}/api/generate", sel.Primary.Uri("api/generate").ToString());
        Assert.Equal($"{Fast}/api/tags", sel.Primary.Uri("api/tags").ToString());
    }

    [Theory]
    [InlineData("")]                       // the shipped default in appsettings.json
    [InlineData("   ")]
    [InlineData("10.20.0.139:11434")]      // the scheme left off
    public void An_unusable_endpoint_is_constructible_and_only_fails_when_called(string endpoint)
    {
        // This is a singleton resolved during startup logging, so throwing in the
        // constructor took the whole application down - and it did so for the most
        // ordinary configuration there is, the AI features simply left switched off.
        var (sel, _) = Build(endpoint: endpoint, fallback: "");

        Assert.False(sel.Primary.IsConfigured);

        // And when something does try to call it, the message says what to set rather
        // than surfacing a bare UriFormatException.
        var ex = Assert.Throws<InvalidOperationException>(() => sel.Primary.Uri("api/tags"));
        Assert.Contains("endpoint", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("http://", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_usable_endpoint_is_reported_as_configured()
    {
        var (sel, _) = Build();
        Assert.True(sel.Primary.IsConfigured);
        Assert.True(sel.Fallback!.IsConfigured);
    }
}
