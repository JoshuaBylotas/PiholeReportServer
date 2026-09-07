using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiholeReportServer.Configuration;
using PiholeReportServer.Models;
using PiholeReportServer.Services;
using Xunit;

namespace PiholeReportServer.Tests;

public class ResultCacheTests
{
    private static ResultCache NewCache(int pageSize = 100) =>
        new(Options.Create(new ReportingOptions { PageSize = pageSize }),
            NullLogger<ResultCache>.Instance);

    private static QueryResult Rows(int count)
    {
        var rows = new List<object?[]>(count);
        for (var i = 0; i < count; i++)
        {
            rows.Add([$"domain{i}.example.com", (long)(count - i), $"client-{i % 7}"]);
        }
        return new QueryResult
        {
            Columns =
            [
                new QueryColumn("domain", "String"),
                new QueryColumn("queries", "Int64"),
                new QueryColumn("client", "String"),
            ],
            Rows = rows,
            RowCap = 100_000,
        };
    }

    [Fact]
    public void Pages_a_large_result_without_returning_all_of_it()
    {
        using var cache = NewCache();
        var token = cache.Store("owner-1", Rows(100_000), "big");

        var first = cache.GetPage("owner-1", token, 0, null);

        Assert.NotNull(first);
        Assert.Equal(100, first!.Rows.Count);          // one page, not 100,000
        Assert.Equal(100_000, first.TotalRows);
        Assert.Equal(1000, first.PageCount);
        Assert.Equal(1, first.FirstRowNumber);
        Assert.Equal(100, first.LastRowNumber);
    }

    [Fact]
    public void Another_user_cannot_read_a_cached_result_with_its_token()
    {
        // The token is the only thing identifying a result, so ownership has to be
        // checked on read or a leaked token would expose someone else's rows.
        using var cache = NewCache();
        var token = cache.Store("owner-1", Rows(500), "mine");

        Assert.NotNull(cache.GetPage("owner-1", token, 0, null));
        Assert.Null(cache.GetPage("owner-2", token, 0, null));
    }

    [Fact]
    public void An_unknown_token_returns_null_rather_than_throwing()
    {
        using var cache = NewCache();
        Assert.Null(cache.GetPage("owner-1", "does-not-exist", 0, null));
    }

    [Fact]
    public void Search_narrows_with_every_additional_term()
    {
        using var cache = NewCache();
        var token = cache.Store("owner-1", Rows(1000), "search");

        var all = cache.GetPage("owner-1", token, 0, null)!;
        var one = cache.GetPage("owner-1", token, 0, "client-3")!;
        var two = cache.GetPage("owner-1", token, 0, "client-3 domain17.")!;

        Assert.Equal(1000, all.MatchingRows);
        Assert.True(one.MatchingRows < all.MatchingRows, "one term should narrow");
        Assert.True(two.MatchingRows < one.MatchingRows, "a second term should narrow further");
        Assert.True(one.IsFiltered);
        Assert.False(all.IsFiltered);
    }

    [Fact]
    public void Search_does_not_match_across_a_column_boundary()
    {
        // Rows are indexed with a separator between cells; without one, the end of
        // one value and the start of the next would concatenate into false matches.
        using var cache = NewCache();
        var result = new QueryResult
        {
            Columns = [new QueryColumn("a", "String"), new QueryColumn("b", "String")],
            Rows = [["abc", "def"]],
            RowCap = 100,
        };
        var token = cache.Store("owner-1", result, "boundary");

        Assert.Equal(1, cache.GetPage("owner-1", token, 0, "abc")!.MatchingRows);
        Assert.Equal(1, cache.GetPage("owner-1", token, 0, "def")!.MatchingRows);
        Assert.Equal(0, cache.GetPage("owner-1", token, 0, "abcdef")!.MatchingRows);
    }

    [Theory]
    [InlineData(-5, 0)]      // before the first page
    [InlineData(0, 0)]
    [InlineData(4, 4)]
    [InlineData(99, 9)]      // past the last page of 10
    public void Page_index_is_clamped_into_range(int requested, int expected)
    {
        using var cache = NewCache();
        var token = cache.Store("owner-1", Rows(1000), "clamp");

        Assert.Equal(expected, cache.GetPage("owner-1", token, requested, null)!.Page);
    }

    [Fact]
    public void An_empty_result_still_yields_a_coherent_page()
    {
        using var cache = NewCache();
        var empty = new QueryResult
        {
            Columns = [new QueryColumn("domain", "String")],
            Rows = [],
            RowCap = 100,
        };
        var token = cache.Store("owner-1", empty, "none");

        var page = cache.GetPage("owner-1", token, 0, null)!;
        Assert.Empty(page.Rows);
        Assert.Equal(1, page.PageCount);       // never zero, so the pager stays sane
        Assert.Equal(0, page.FirstRowNumber);
        Assert.Equal(0, page.LastRowNumber);
    }

    [Fact]
    public void The_last_page_is_short_rather_than_padded()
    {
        using var cache = NewCache(pageSize: 100);
        var token = cache.Store("owner-1", Rows(250), "partial");

        var last = cache.GetPage("owner-1", token, 2, null)!;
        Assert.Equal(50, last.Rows.Count);
        Assert.Equal(201, last.FirstRowNumber);
        Assert.Equal(250, last.LastRowNumber);
    }

    [Fact]
    public void Truncation_is_carried_through_to_the_page()
    {
        using var cache = NewCache();
        var capped = new QueryResult
        {
            Columns = [new QueryColumn("d", "String")],
            Rows = [["x"]],
            Truncated = true,
            RowCap = 100_000,
        };
        var token = cache.Store("owner-1", capped, "capped");

        var page = cache.GetPage("owner-1", token, 0, null)!;
        Assert.True(page.Truncated);
        Assert.Equal(100_000, page.RowCap);
    }
}
