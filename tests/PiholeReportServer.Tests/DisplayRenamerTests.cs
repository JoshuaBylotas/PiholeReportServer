using PiholeReportServer.Models;
using PiholeReportServer.Services;
using Xunit;

namespace PiholeReportServer.Tests;

/// <summary>
/// Renaming is display-only, and the risk is that it is either too eager (rewriting
/// a domain that merely resembles a hostname) or too timid (missing the FQDN form,
/// which is the whole reason it exists). These pin the boundary.
/// </summary>
public class DisplayRenamerTests
{
    private static AnalystFact Rename(string display, string match) => new()
    {
        Kind = "rename",
        Subject = display,
        Target = match,
        Fact = $"Show {match} as {display}",
    };

    private static QueryResult Result(params object?[][] rows) => new()
    {
        Columns = [new QueryColumn("device", "String"), new QueryColumn("n", "Int64")],
        Rows = rows,
        RowCap = 1000,
    };

    [Theory]
    // A short pattern catches every reverse-DNS variation of the same host. This is
    // the point: one machine appearing as three rows reads as three devices.
    [InlineData("ALIEN01", "ALIEN01", true)]
    [InlineData("ALIEN01", "ALIEN01.bylotas.net", true)]
    [InlineData("ALIEN01", "alien01.BYLOTAS.NET", true)]
    [InlineData("ALIEN01", "alien01", true)]
    // But not a different host that merely starts with the same letters.
    [InlineData("ALIEN01", "ALIEN011", false)]
    [InlineData("ALIEN01", "ALIEN01-DESKTOP", false)]
    [InlineData("ALIEN01", "ALIEN01-DESKTOP.bylotas.net", false)]
    public void A_short_pattern_matches_the_host_and_its_fqdn_forms(
        string pattern, string value, bool expected)
    {
        Assert.Equal(expected, DisplayRenamer.Matches(value, pattern));
    }

    [Theory]
    // A dotted pattern is exact, so it can be used when the loose form would catch
    // too much.
    [InlineData("alien01.bylotas.net", "ALIEN01.BYLOTAS.NET", true)]
    [InlineData("alien01.bylotas.net", "alien01", false)]
    [InlineData("alien01.bylotas.net", "alien01.example.com", false)]
    public void A_dotted_pattern_matches_only_the_whole_value(
        string pattern, string value, bool expected)
    {
        Assert.Equal(expected, DisplayRenamer.Matches(value, pattern));
    }

    [Fact]
    public void The_longest_matching_rule_wins()
    {
        // So a precise FQDN rule can override a broad short-name one for one host.
        var rules = DisplayRenamer.RulesFrom(
        [
            Rename("Any Alien", "alien01"),
            Rename("The Work Alien", "alien01.bylotas.net"),
        ]);

        Assert.Equal("The Work Alien", DisplayRenamer.Apply("alien01.bylotas.net", rules));
        Assert.Equal("Any Alien", DisplayRenamer.Apply("alien01.example.com", rules));
    }

    [Fact]
    public void Every_variation_collapses_to_one_label_in_the_output()
    {
        var rules = DisplayRenamer.RulesFrom([Rename("Living Room PC", "ALIEN01")]);
        var result = DisplayRenamer.Rename(Result(
            ["ALIEN01", 10L],
            ["ALIEN01.bylotas.net", 20L],
            ["alien01.BYLOTAS.NET", 30L],
            ["OTHER-PC", 40L]), rules);

        Assert.Equal("Living Room PC", result.Rows[0][0]);
        Assert.Equal("Living Room PC", result.Rows[1][0]);
        Assert.Equal("Living Room PC", result.Rows[2][0]);
        Assert.Equal("OTHER-PC", result.Rows[3][0]);
    }

    [Fact]
    public void Renaming_does_not_merge_rows_or_change_any_number()
    {
        // Renaming is not a GROUP BY. Three rows that now share a label stay three
        // rows: collapsing them would silently change the totals, and the whole
        // safety argument for a display-only feature is that it cannot do that.
        var rules = DisplayRenamer.RulesFrom([Rename("Living Room PC", "ALIEN01")]);
        var result = DisplayRenamer.Rename(Result(
            ["ALIEN01", 10L],
            ["ALIEN01.bylotas.net", 20L]), rules);

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(10L, result.Rows[0][1]);
        Assert.Equal(20L, result.Rows[1][1]);
    }

    [Fact]
    public void Non_string_cells_are_left_alone()
    {
        // A rule whose text happens to look like a number must not touch a count.
        var rules = DisplayRenamer.RulesFrom([Rename("ten", "10")]);
        var result = DisplayRenamer.Rename(Result(["host", 10L]), rules);

        Assert.Equal(10L, result.Rows[0][1]);
    }

    [Fact]
    public void Nulls_survive_untouched()
    {
        var rules = DisplayRenamer.RulesFrom([Rename("X", "ALIEN01")]);
        var result = DisplayRenamer.Rename(Result([null, 1L]), rules);

        Assert.Null(result.Rows[0][0]);
    }

    [Fact]
    public void With_no_rules_the_result_is_returned_unchanged_without_copying()
    {
        // A 100,000-row result must not be duplicated for nothing.
        var original = Result(["ALIEN01", 1L]);
        Assert.Same(original, DisplayRenamer.Rename(original, []));
    }

    [Fact]
    public void A_result_that_matches_nothing_is_also_returned_unchanged()
    {
        var original = Result(["OTHER-PC", 1L]);
        var rules = DisplayRenamer.RulesFrom([Rename("X", "ALIEN01")]);
        Assert.Same(original, DisplayRenamer.Rename(original, rules));
    }

    [Fact]
    public void Truncation_and_the_cap_survive_the_rewrite()
    {
        var rules = DisplayRenamer.RulesFrom([Rename("Living Room PC", "ALIEN01")]);
        var capped = new QueryResult
        {
            Columns = [new QueryColumn("device", "String")],
            Rows = [["ALIEN01"]],
            Truncated = true,
            RowCap = 100_000,
        };

        var renamed = DisplayRenamer.Rename(capped, rules);
        Assert.True(renamed.Truncated);
        Assert.Equal(100_000, renamed.RowCap);
    }

    [Fact]
    public void Facts_of_other_kinds_produce_no_rules()
    {
        var rules = DisplayRenamer.RulesFrom(
        [
            new AnalystFact { Kind = "device", Subject = "Jason's phone",
                              Target = "Pixel-9-Pro-XL", Fact = "x" },
            new AnalystFact { Kind = "preference", Subject = "ordering",
                              Fact = "always order descending" },
        ]);

        Assert.Empty(rules);
    }
}
