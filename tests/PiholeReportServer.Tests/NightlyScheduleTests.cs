using PiholeReportServer.Services;
using Xunit;

namespace PiholeReportServer.Tests;

/// <summary>
/// The schedule arithmetic is the part of the nightly job that can silently misbehave:
/// get it wrong and the battery either never runs or runs continuously.
/// </summary>
public class NightlyScheduleTests
{
    [Fact]
    public void Waits_until_later_today_when_the_hour_has_not_passed()
    {
        var now = new DateTime(2026, 9, 7, 1, 30, 0);
        Assert.Equal(TimeSpan.FromMinutes(90), NightlyAnalysisService.UntilNextRun(now, 3));
    }

    [Fact]
    public void Waits_until_tomorrow_when_the_hour_has_passed()
    {
        var now = new DateTime(2026, 9, 7, 4, 0, 0);
        Assert.Equal(TimeSpan.FromHours(23), NightlyAnalysisService.UntilNextRun(now, 3));
    }

    [Fact]
    public void Never_returns_zero_or_negative_at_exactly_the_target_hour()
    {
        // Starting the service exactly on the hour must schedule tomorrow, not fire
        // immediately and then loop with a zero delay.
        var now = new DateTime(2026, 9, 7, 3, 0, 0);
        var delay = NightlyAnalysisService.UntilNextRun(now, 3);
        Assert.True(delay > TimeSpan.Zero, $"delay was {delay}");
        Assert.Equal(TimeSpan.FromHours(24), delay);
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(24)]
    [InlineData(99)]
    public void Clamps_an_out_of_range_hour_rather_than_throwing(int hour)
    {
        var now = new DateTime(2026, 9, 7, 12, 0, 0);
        var delay = NightlyAnalysisService.UntilNextRun(now, hour);
        Assert.InRange(delay, TimeSpan.Zero, TimeSpan.FromHours(24));
    }

    [Fact]
    public void Handles_midnight_as_a_valid_hour()
    {
        var now = new DateTime(2026, 9, 7, 22, 15, 0);
        Assert.Equal(TimeSpan.FromMinutes(105), NightlyAnalysisService.UntilNextRun(now, 0));
    }
}
