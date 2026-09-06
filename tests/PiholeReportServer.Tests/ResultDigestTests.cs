using PiholeReportServer.Models;
using PiholeReportServer.Services;
using Xunit;

namespace PiholeReportServer.Tests;

public class ResultDigestTests
{
    private static QueryResult Result(
        (string Name, string Type)[] columns,
        object?[][] rows,
        bool truncated = false) => new()
        {
            Columns = columns.Select(c => new QueryColumn(c.Name, c.Type)).ToList(),
            Rows = rows,
            Truncated = truncated,
            RowCap = 5000,
        };

    [Fact]
    public void Summarises_a_numeric_column_exactly()
    {
        var r = Result(
            [("domain", "String"), ("queries", "Int64")],
            [
                ["a.example.com", 100L],
                ["b.example.com", 200L],
                ["c.example.com", 300L],
            ]);

        var digest = ResultDigest.Build(r, "top domains");

        // The point of computing this locally is that the figures are exact; the
        // model is only asked to narrate them.
        Assert.Contains("total 600", digest);
        Assert.Contains("average 200", digest);
        Assert.Contains("range 100–300", digest);
    }

    [Fact]
    public void Lists_the_most_frequent_values_of_a_text_column()
    {
        var rows = new List<object?[]>();
        for (var i = 0; i < 10; i++) { rows.Add(["device-a"]); }
        for (var i = 0; i < 4; i++) { rows.Add(["device-b"]); }
        rows.Add(["device-c"]);

        var digest = ResultDigest.Build(
            Result([("client", "String")], rows.ToArray()), "clients");

        Assert.Contains("3 distinct", digest);
        Assert.Contains("device-a (10)", digest);
        Assert.Contains("device-b (4)", digest);
    }

    [Fact]
    public void Reports_truncation_so_the_model_does_not_present_partial_totals_as_complete()
    {
        var digest = ResultDigest.Build(
            Result([("queries", "Int64")], [[1L]], truncated: true), "x");

        Assert.Contains("truncated", digest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("partial", digest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Counts_nulls_separately_rather_than_as_a_value()
    {
        var digest = ResultDigest.Build(
            Result([("forward", "String")], [["1.1.1.1"], [null], [null]]), "upstreams");

        Assert.Contains("2 null", digest);
        Assert.Contains("1 distinct", digest);
    }

    [Fact]
    public void Handles_an_all_null_numeric_column_without_throwing()
    {
        var digest = ResultDigest.Build(
            Result([("reply_time", "Double")], [[null], [null]]), "timings");

        Assert.Contains("no numeric values", digest);
    }

    [Fact]
    public void Stays_compact_for_a_large_result()
    {
        // The whole reason the digest exists: sending rows was measured at ~150s for
        // 100 rows. The digest must not grow with row count.
        var rows = Enumerable.Range(0, 20_000)
            .Select(i => new object?[] { $"domain{i % 500}.example.com", (long)i })
            .ToArray();

        var digest = ResultDigest.Build(
            Result([("domain", "String"), ("queries", "Int64")], rows), "big");

        // Roughly 4 characters per token, so this keeps the prompt well under a
        // second of evaluation on the inference host.
        Assert.True(digest.Length < 1200, $"digest was {digest.Length} chars");
        Assert.Contains("20,000", digest);
    }

    [Fact]
    public void Truncates_a_very_wide_result_rather_than_inflating_the_prompt()
    {
        var columns = Enumerable.Range(0, 20).Select(i => ($"col{i}", "String")).ToArray();
        var row = Enumerable.Range(0, 20).Select(i => (object?)$"v{i}").ToArray();

        var digest = ResultDigest.Build(Result(columns, [row]), "wide");

        Assert.Contains("describing the first 8", digest);
        Assert.DoesNotContain("col9:", digest);
    }
}
