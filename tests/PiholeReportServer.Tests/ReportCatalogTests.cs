using PiholeReportServer.Models;
using PiholeReportServer.Services;
using Xunit;

namespace PiholeReportServer.Tests;

public class ReportCatalogTests
{
    private static readonly DateTime Now = new(2026, 9, 3, 14, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Resolves_today_and_now()
    {
        var date = new ReportParameter { Kind = ParameterKind.Date, Default = "today" };
        Assert.Equal("2026-09-03", ReportCatalog.ResolveDefault(date, Now));

        var dt = new ReportParameter { Kind = ParameterKind.DateTime, Default = "now" };
        Assert.Equal("2026-09-03T14:30", ReportCatalog.ResolveDefault(dt, Now));
    }

    [Theory]
    [InlineData("-7d", "2026-08-27T14:30")]
    [InlineData("-24h", "2026-09-02T14:30")]
    [InlineData("+1d", "2026-09-04T14:30")]
    public void Resolves_relative_offsets(string spec, string expected)
    {
        var p = new ReportParameter { Kind = ParameterKind.DateTime, Default = spec };
        Assert.Equal(expected, ReportCatalog.ResolveDefault(p, Now));
    }

    [Fact]
    public void Resolves_a_relative_date_to_midnight()
    {
        var p = new ReportParameter { Kind = ParameterKind.Date, Default = "-7d" };
        Assert.Equal("2026-08-27", ReportCatalog.ResolveDefault(p, Now));
    }

    [Fact]
    public void Passes_through_a_literal_default()
    {
        var p = new ReportParameter { Kind = ParameterKind.Int, Default = "100" };
        Assert.Equal("100", ReportCatalog.ResolveDefault(p, Now));
    }

    [Fact]
    public void Returns_null_when_no_default_is_declared()
    {
        Assert.Null(ReportCatalog.ResolveDefault(new ReportParameter(), Now));
    }
}
