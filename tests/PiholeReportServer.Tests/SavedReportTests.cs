using System.Security.Claims;
using System.Text.Json;
using PiholeReportServer.Models;
using PiholeReportServer.Pages.Query;
using PiholeReportServer.Services;
using Xunit;
using static PiholeReportServer.Models.BuilderSpec;

namespace PiholeReportServer.Tests;

public class SavedReportOwnerTests
{
    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    [Fact]
    public void Prefers_the_immutable_entra_object_id()
    {
        var user = Principal(
            ("oid", "064aa343-cee7-4e26-b737-466840e96b36"),
            ("preferred_username", "someone@example.com"));

        Assert.Equal("064aa343-cee7-4e26-b737-466840e96b36", SavedReportStore.OwnerOid(user));
    }

    [Fact]
    public void Falls_back_to_the_long_form_objectidentifier_claim()
    {
        var user = Principal(
            ("http://schemas.microsoft.com/identity/claims/objectidentifier", "abc-123"));

        Assert.Equal("abc-123", SavedReportStore.OwnerOid(user));
    }

    [Fact]
    public void Falls_back_to_upn_when_no_object_id_is_present()
    {
        var user = Principal(("preferred_username", "someone@example.com"));
        Assert.Equal("someone@example.com", SavedReportStore.OwnerOid(user));
    }

    [Fact]
    public void Returns_null_when_the_principal_carries_nothing_usable()
    {
        Assert.Null(SavedReportStore.OwnerOid(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    [Fact]
    public void Owner_name_prefers_the_display_name()
    {
        var user = Principal(("name", "Joshua Bylotas"), ("preferred_username", "j@example.com"));
        Assert.Equal("Joshua Bylotas", SavedReportStore.OwnerName(user));
    }
}

public class SavedBuilderSpecRoundTripTests
{
    private static BuilderSpec RoundTrip(BuilderSpec spec)
    {
        var json = JsonSerializer.Serialize(spec, BuilderModel.SpecJson);
        var back = BuilderModel.Deserialise(json);
        Assert.NotNull(back);
        return back!;
    }

    [Fact]
    public void Every_field_survives_a_round_trip()
    {
        var original = new BuilderSpec
        {
            From = new DateTime(2026, 9, 1, 6, 30, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 9, 4, 6, 30, 0, DateTimeKind.Utc),
            GroupBy = GroupDimension.Blocklist,
            ThenBy = GroupDimension.ClientHostname,
            Metric = MetricKind.AvgReplyMs,
            ClientFilter = "192.0.2.",
            DomainFilter = "doubleclick",
            ClientIps = ["192.0.2.10", "192.0.2.11"],
            BlocklistIds = [9, 10],
            StatusFilter = 2,
            TypeFilter = 65,
            Sort = SortDirection.Asc,
            Limit = 750,
        };

        var back = RoundTrip(original);

        Assert.Equal(original.From, back.From);
        Assert.Equal(original.To, back.To);
        Assert.Equal(original.GroupBy, back.GroupBy);
        Assert.Equal(original.ThenBy, back.ThenBy);
        Assert.Equal(original.Metric, back.Metric);
        Assert.Equal(original.ClientFilter, back.ClientFilter);
        Assert.Equal(original.DomainFilter, back.DomainFilter);
        Assert.Equal(original.ClientIps, back.ClientIps);
        Assert.Equal(original.BlocklistIds, back.BlocklistIds);
        Assert.Equal(original.StatusFilter, back.StatusFilter);
        Assert.Equal(original.TypeFilter, back.TypeFilter);
        Assert.Equal(original.Sort, back.Sort);
        Assert.Equal(original.Limit, back.Limit);
    }

    [Fact]
    public void Enums_are_stored_by_name_not_ordinal()
    {
        // Ordinals would silently re-point at a different column the moment somebody
        // inserts a new dimension into the middle of the enum, so every saved report
        // would quietly start reporting on the wrong thing.
        var json = JsonSerializer.Serialize(
            new BuilderSpec { GroupBy = GroupDimension.Blocklist, Metric = MetricKind.MaxReplyMs },
            BuilderModel.SpecJson);

        Assert.Contains("\"Blocklist\"", json);
        Assert.Contains("\"MaxReplyMs\"", json);
    }

    [Fact]
    public void A_round_tripped_spec_composes_to_identical_sql()
    {
        var original = new BuilderSpec
        {
            GroupBy = GroupDimension.Domain,
            ThenBy = GroupDimension.Day,
            Metric = MetricKind.QueryCount,
            BlocklistIds = [9],
            ClientIps = ["192.0.2.10"],
            Limit = 123,
        };

        var before = BuilderSqlComposer.Compose(original, 5000);
        var after = BuilderSqlComposer.Compose(RoundTrip(original), 5000);

        Assert.Equal(before.Sql, after.Sql);
        Assert.Equal(before.Parameters.Count, after.Parameters.Count);
    }

    [Fact]
    public void Invalid_stored_json_is_reported_rather_than_thrown()
    {
        Assert.Null(BuilderModel.Deserialise("{ this is not json"));
        Assert.Null(BuilderModel.Deserialise("[]"));
    }

    [Fact]
    public void An_unknown_enum_name_does_not_crash_the_load()
    {
        // A report saved by a newer build that had an extra dimension must fail
        // cleanly, not throw an unhandled exception on someone's dashboard.
        var back = BuilderModel.Deserialise("""{"GroupBy":"SomethingRemoved"}""");
        Assert.Null(back);
    }
}
