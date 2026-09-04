using PiholeReportServer.Models;
using PiholeReportServer.Services;
using Xunit;
using static PiholeReportServer.Models.BuilderSpec;

namespace PiholeReportServer.Tests;

public class BuilderSqlComposerTests
{
    [Fact]
    public void Composed_sql_passes_the_guard()
    {
        // Whatever the builder emits must itself be a legal read-only statement.
        foreach (var dim in Enum.GetValues<GroupDimension>())
        {
            foreach (var metric in Enum.GetValues<MetricKind>())
            {
                var spec = new BuilderSpec { GroupBy = dim, Metric = metric };
                var composed = BuilderSqlComposer.Compose(spec, 5000);
                var verdict = SqlGuard.Validate(composed.Sql);
                Assert.True(verdict.Allowed, $"{dim}/{metric}: {verdict.Reason}\n{composed.Sql}");
            }
        }
    }

    [Fact]
    public void Free_text_filters_never_reach_the_sql()
    {
        var spec = new BuilderSpec
        {
            DomainFilter = "'; DROP TABLE dbo.PiholeQueries --",
            ClientFilter = "192.0.2.1' OR 1=1 --",
        };

        var composed = BuilderSqlComposer.Compose(spec, 5000);

        Assert.DoesNotContain("DROP", composed.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OR 1=1", composed.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@domainFilter", composed.Sql);
        Assert.Contains("@clientFilter", composed.Sql);

        // The hostile text survives only as a bound parameter value.
        Assert.Contains("DROP TABLE", (string)composed.Parameters["domainFilter"]!);
    }

    [Fact]
    public void Like_wildcards_in_a_filter_are_escaped()
    {
        var spec = new BuilderSpec { DomainFilter = "100%_off[x]" };
        var composed = BuilderSqlComposer.Compose(spec, 5000);

        var value = (string)composed.Parameters["domainFilter"]!;
        Assert.Contains("[%]", value);
        Assert.Contains("[_]", value);
        Assert.Contains("[[]", value);
    }

    [Fact]
    public void Row_limit_is_clamped_to_the_hard_cap()
    {
        var spec = new BuilderSpec { Limit = 999_999 };
        var composed = BuilderSqlComposer.Compose(spec, 5000);
        Assert.Equal(5000, composed.Parameters["limit"]);
    }

    [Fact]
    public void Row_limit_below_one_is_raised_to_one()
    {
        var spec = new BuilderSpec { Limit = 0 };
        var composed = BuilderSqlComposer.Compose(spec, 5000);
        Assert.Equal(1, composed.Parameters["limit"]);
    }

    [Fact]
    public void Date_bounds_become_parameters_when_supplied()
    {
        var from = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);

        var composed = BuilderSqlComposer.Compose(new BuilderSpec { From = from, To = to }, 5000);

        Assert.Contains("q.ts >= @from", composed.Sql);
        Assert.Contains("q.ts < @to", composed.Sql);
        Assert.Equal(from, composed.Parameters["from"]);
        Assert.Equal(to, composed.Parameters["to"]);
    }

    [Fact]
    public void Dimension_joins_are_only_added_when_needed()
    {
        var plain = BuilderSqlComposer.Compose(
            new BuilderSpec { GroupBy = GroupDimension.Domain }, 5000);
        Assert.DoesNotContain("DimClient", plain.Sql);

        var withHostname = BuilderSqlComposer.Compose(
            new BuilderSpec { GroupBy = GroupDimension.ClientHostname }, 5000);
        Assert.Contains("DimClient", withHostname.Sql);
    }

    [Fact]
    public void Duplicate_then_by_is_ignored()
    {
        var composed = BuilderSqlComposer.Compose(
            new BuilderSpec { GroupBy = GroupDimension.Domain, ThenBy = GroupDimension.Domain },
            5000);

        // "q.domain" should appear once in the GROUP BY, not twice.
        var groupBy = composed.Sql
            .Split('\n')
            .First(l => l.TrimStart().StartsWith("GROUP BY", StringComparison.Ordinal));
        Assert.Equal(1, groupBy.Split("q.domain").Length - 1);
    }

    [Fact]
    public void Blocklist_filter_adds_an_exists_predicate()
    {
        var composed = BuilderSqlComposer.Compose(
            new BuilderSpec { OnlyBlocklisted = true }, 5000);
        Assert.Contains("GravityDomains", composed.Sql);
        Assert.Contains("EXISTS", composed.Sql);
    }

    [Fact]
    public void Selected_clients_become_an_in_list_of_bound_parameters()
    {
        var spec = new BuilderSpec
        {
            ClientIps = ["192.0.2.10", "192.0.2.11", "192.0.2.12"],
        };

        var composed = BuilderSqlComposer.Compose(spec, 5000);

        Assert.Contains("q.client IN (@cli0, @cli1, @cli2)", composed.Sql);
        Assert.Equal("192.0.2.10", composed.Parameters["cli0"]);
        Assert.Equal("192.0.2.11", composed.Parameters["cli1"]);
        Assert.Equal("192.0.2.12", composed.Parameters["cli2"]);
        Assert.True(SqlGuard.Validate(composed.Sql).Allowed);
    }

    [Fact]
    public void No_selected_clients_means_no_in_clause()
    {
        var composed = BuilderSqlComposer.Compose(new BuilderSpec(), 5000);
        Assert.DoesNotContain("q.client IN", composed.Sql);
    }

    [Fact]
    public void Hostile_client_values_stay_parameters_and_never_reach_the_sql()
    {
        // A tampered form post is the threat here, since the picker itself only
        // offers known IPs.
        var spec = new BuilderSpec
        {
            ClientIps = ["192.0.2.1') OR 1=1 --", "'; DROP TABLE dbo.PiholeQueries --"],
        };

        var composed = BuilderSqlComposer.Compose(spec, 5000);

        Assert.DoesNotContain("DROP", composed.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OR 1=1", composed.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("q.client IN (@cli0, @cli1)", composed.Sql);
        Assert.True(SqlGuard.Validate(composed.Sql).Allowed);
    }

    [Fact]
    public void Duplicate_and_blank_client_selections_are_collapsed()
    {
        var spec = new BuilderSpec
        {
            ClientIps = ["192.0.2.10", "192.0.2.10", "", "  ", "192.0.2.11"],
        };

        var composed = BuilderSqlComposer.Compose(spec, 5000);

        Assert.Contains("q.client IN (@cli0, @cli1)", composed.Sql);
        Assert.DoesNotContain("@cli2", composed.Sql);
    }

    [Fact]
    public void Client_selection_is_capped()
    {
        var spec = new BuilderSpec
        {
            ClientIps = Enumerable.Range(0, BuilderSqlComposer.MaxSelectedClients + 250)
                                  .Select(i => $"10.0.{i / 256}.{i % 256}")
                                  .ToList(),
        };

        var composed = BuilderSqlComposer.Compose(spec, 5000);

        var bound = composed.Parameters.Keys.Count(k => k.StartsWith("cli", StringComparison.Ordinal));
        Assert.Equal(BuilderSqlComposer.MaxSelectedClients, bound);
    }

    [Fact]
    public void Client_picker_and_contains_filter_combine()
    {
        var spec = new BuilderSpec
        {
            ClientIps = ["192.0.2.10"],
            ClientFilter = "192.0.2.",
        };

        var composed = BuilderSqlComposer.Compose(spec, 5000);

        Assert.Contains("q.client LIKE @clientFilter", composed.Sql);
        Assert.Contains("q.client IN (@cli0)", composed.Sql);
    }
}
