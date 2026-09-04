using System.Globalization;
using Xunit;

namespace PiholeReportServer.Tests;

/// <summary>
/// Guards the one build property that breaks the application at runtime while
/// leaving the build green.
/// </summary>
public class GlobalizationTests
{
    [Fact]
    public void Globalization_invariant_mode_must_be_off()
    {
        // Microsoft.Data.SqlClient throws "Globalization Invariant Mode is not
        // supported" the moment it opens a connection, so with this switch on
        // every report, the builder and the SQL console fail — but nothing in a
        // normal build or test run notices. Directory.Build.props applies to
        // this assembly too, so asserting it here catches a regression.
        AppContext.TryGetSwitch("System.Globalization.Invariant", out var invariant);
        Assert.False(
            invariant,
            "InvariantGlobalization is enabled. Microsoft.Data.SqlClient cannot open a " +
            "connection in that mode. Set <InvariantGlobalization>false</InvariantGlobalization> " +
            "in Directory.Build.props.");
    }

    [Fact]
    public void Culture_data_is_actually_available()
    {
        // A second, behavioural check: in invariant mode every culture collapses
        // to the invariant one, so this lookup stops reflecting the real culture.
        var culture = CultureInfo.GetCultureInfo("en-GB");
        Assert.Equal("en-GB", culture.Name);
        Assert.NotEqual(CultureInfo.InvariantCulture.NumberFormat.CurrencySymbol,
                        culture.NumberFormat.CurrencySymbol);
    }
}
