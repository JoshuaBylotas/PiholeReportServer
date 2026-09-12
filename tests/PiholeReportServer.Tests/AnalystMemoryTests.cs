using PiholeReportServer.Models;
using PiholeReportServer.Services;
using Xunit;

namespace PiholeReportServer.Tests;

/// <summary>
/// The memory is what lets someone ask about "Jason's phone" when the warehouse only
/// knows <c>Pixel-9-Pro-XL</c>. What matters is that the fact reaches the model as a
/// filter it can actually use — a mapping in prose gets rebuilt wrongly, and the
/// wrong clause matches nothing at all while looking perfectly reasonable.
/// </summary>
public class AnalystMemoryTests
{
    private static AnalystFact Device(string subject, string target) => new()
    {
        Kind = "device",
        Subject = subject,
        Target = target,
        Fact = $"{subject} is the device {target}",
    };

    private static AnalystFact Note(string text) => new()
    {
        Kind = "note",
        Subject = text,
        Fact = text,
    };

    [Fact]
    public void No_facts_renders_no_fact_list()
    {
        var text = AnalystMemoryStore.Render([]);

        // An empty section would still cost prompt tokens and invite the model to
        // invent entries to fill it.
        Assert.DoesNotContain("WHAT YOU HAVE BEEN TOLD", text);
        Assert.DoesNotContain("Devices:", text);
        Assert.DoesNotContain("Other facts:", text);
    }

    [Fact]
    public void How_to_remember_is_present_even_with_nothing_remembered()
    {
        // This lived inside the empty-facts early return, so a model with nothing
        // remembered was never told it could remember. The first fact could then only
        // be taught through the panel - at exactly the moment someone is most likely
        // to try telling it in conversation instead.
        var text = AnalystMemoryStore.Render([]);

        Assert.Contains("\"action\":\"remember\"", text);
        Assert.Contains("\"action\":\"forget\"", text);
    }

    [Fact]
    public void How_to_remember_survives_alongside_existing_facts()
    {
        var text = AnalystMemoryStore.Render([Device("Jason's phone", "Pixel-9-Pro-XL")]);

        Assert.Contains("WHAT YOU HAVE BEEN TOLD", text);
        Assert.Contains("\"action\":\"remember\"", text);
        Assert.Contains("\"action\":\"forget\"", text);
    }

    [Fact]
    public void A_device_fact_carries_the_filter_not_just_the_mapping()
    {
        var text = AnalystMemoryStore.Render([Device("Jason's phone", "Pixel-9-Pro-XL")]);

        Assert.Contains("Jason's phone", text);
        Assert.Contains("Pixel-9-Pro-XL", text);
        // The clause is the point: handed one, the model reuses it reliably.
        Assert.Contains("dc.name LIKE 'Pixel-9-Pro-XL%'", text);
    }

    [Fact]
    public void Devices_are_listed_before_general_notes()
    {
        var text = AnalystMemoryStore.Render(
        [
            Note("The guest VLAN is 10.20.9.x"),
            Device("the TV", "Samsung-TV"),
        ]);

        Assert.True(text.IndexOf("Devices:", StringComparison.Ordinal)
                  < text.IndexOf("Other facts:", StringComparison.Ordinal),
            "device aliases decide whether a question is answerable, so they lead");
    }

    [Fact]
    public void The_prompt_explains_how_to_be_taught_something_new()
    {
        var text = AnalystMemoryStore.Render([Device("the TV", "Samsung-TV")]);
        Assert.Contains("\"action\":\"remember\"", text);
    }

    [Theory]
    // A hostname is matched loosely: reverse DNS suffixes it with the domain, so an
    // equality test on the bare name finds nothing.
    [InlineData("Pixel-9-Pro-XL", "dc.name LIKE 'Pixel-9-Pro-XL%'")]
    [InlineData("WINSERVER01", "dc.name LIKE 'WINSERVER01%'")]
    // An IP is exact, and joins through the fact table's own column.
    [InlineData("10.20.0.100", "q.client = '10.20.0.100'")]
    [InlineData("fd9b:25cc:e1bc:6d52::1", "q.client = 'fd9b:25cc:e1bc:6d52::1'")]
    // A MAC is the device's real identity and survives a DHCP change.
    [InlineData("e0:d3:62:94:9b:90", "dc.mac = 'e0:d3:62:94:9b:90'")]
    [InlineData("E0:D3:62:94:9B:90", "dc.mac = 'e0:d3:62:94:9b:90'")]
    public void The_filter_matches_what_kind_of_identifier_it_was_given(string target, string expected)
    {
        Assert.Equal(expected, AnalystMemoryStore.FilterFor(target));
    }

    [Fact]
    public void An_ipv6_address_is_not_mistaken_for_a_mac()
    {
        // Both are colon-separated, and an IPv6 address has more colons than a MAC.
        // Getting this backwards would emit dc.mac = '<ipv6>', which matches nothing.
        Assert.StartsWith("q.client", AnalystMemoryStore.FilterFor("fd9b:25cc:e1bc:6d52:4de9:3213:dd52:bd95"));
    }

    [Fact]
    public void A_quote_in_a_hostname_cannot_break_out_of_the_literal()
    {
        // The filter is interpolated into prompt text that the model copies into SQL,
        // so an unescaped quote would produce a broken or altered query.
        var clause = AnalystMemoryStore.FilterFor("weird'name");
        Assert.Equal("dc.name LIKE 'weird''name%'", clause);
    }

    [Fact]
    public void A_mac_written_with_dashes_is_still_recognised()
    {
        Assert.Equal("dc.mac = 'e0-d3-62-94-9b-90'",
            AnalystMemoryStore.FilterFor("E0-D3-62-94-9B-90"));
    }
}
