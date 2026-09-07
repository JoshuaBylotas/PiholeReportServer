using PiholeReportServer.Services;
using Xunit;

namespace PiholeReportServer.Tests;

public class SqlGuardTests
{
    [Theory]
    [InlineData("SELECT TOP (10) * FROM dbo.PiholeQueries")]
    [InlineData("select domain, count_big(*) from dbo.PiholeQueries group by domain")]
    [InlineData("WITH x AS (SELECT 1 AS a) SELECT a FROM x")]
    [InlineData("SELECT TOP (5) * FROM dbo.PiholeQueries ORDER BY ts DESC;")]
    [InlineData("  \n SELECT 1 ")]
    public void Allows_read_only_statements(string sql)
    {
        var r = SqlGuard.Validate(sql);
        Assert.True(r.Allowed, r.Reason);
    }

    [Theory]
    [InlineData("DELETE FROM dbo.PiholeQueries")]
    [InlineData("UPDATE dbo.PiholeQueries SET domain = 'x'")]
    [InlineData("INSERT INTO dbo.PiholeQueries (id) VALUES (1)")]
    [InlineData("DROP TABLE dbo.PiholeQueries")]
    [InlineData("TRUNCATE TABLE dbo.PiholeQueries")]
    [InlineData("ALTER TABLE dbo.PiholeQueries ADD x int")]
    [InlineData("EXEC sp_who")]
    [InlineData("SELECT * INTO #tmp FROM dbo.PiholeQueries")]
    [InlineData("GRANT CONTROL ON DATABASE::pihole TO public")]
    [InlineData("WAITFOR DELAY '00:10:00'")]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', '', 'SELECT 1')")]
    public void Blocks_writes_and_dangerous_verbs(string sql)
    {
        var r = SqlGuard.Validate(sql);
        Assert.False(r.Allowed);
        Assert.NotNull(r.Reason);
    }

    [Fact]
    public void Blocks_backup_statements()
    {
        var r = SqlGuard.Validate(@"BACKUP DATABASE pihole TO DISK = 'c:\x.bak'");
        Assert.False(r.Allowed);
    }

    [Fact]
    public void Blocks_stacked_statements()
    {
        var r = SqlGuard.Validate("SELECT 1; DROP TABLE dbo.PiholeQueries");
        Assert.False(r.Allowed);
        Assert.Contains("one statement", r.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tolerates_a_single_trailing_semicolon()
    {
        Assert.True(SqlGuard.Validate("SELECT 1;").Allowed);
        Assert.True(SqlGuard.Validate("SELECT 1;   ").Allowed);
    }

    [Fact]
    public void Blocks_statements_hidden_behind_a_comment()
    {
        // The dangerous verb is the real code; the SELECT is a decoy inside a comment.
        var r = SqlGuard.Validate("/* SELECT */ DELETE FROM dbo.PiholeQueries");
        Assert.False(r.Allowed);
    }

    [Fact]
    public void Does_not_trip_on_keywords_inside_string_literals()
    {
        // A domain that merely contains "delete" must not be mistaken for DML.
        var r = SqlGuard.Validate(
            "SELECT * FROM dbo.PiholeQueries WHERE domain = 'delete-me.example.com'");
        Assert.True(r.Allowed, r.Reason);
    }

    [Fact]
    public void Does_not_trip_on_keywords_inside_bracketed_identifiers()
    {
        var r = SqlGuard.Validate("SELECT [update] FROM dbo.SomeView");
        Assert.True(r.Allowed, r.Reason);
    }

    [Fact]
    public void Blocks_comment_only_and_empty_input()
    {
        Assert.False(SqlGuard.Validate("").Allowed);
        Assert.False(SqlGuard.Validate("   ").Allowed);
        Assert.False(SqlGuard.Validate(null).Allowed);
        Assert.False(SqlGuard.Validate("-- just a comment").Allowed);
        Assert.False(SqlGuard.Validate("/* nothing here */").Allowed);
    }

    [Fact]
    public void Blocks_four_part_linked_server_names()
    {
        var r = SqlGuard.Validate("SELECT * FROM remote.pihole.dbo.PiholeQueries");
        Assert.False(r.Allowed);
        Assert.Contains("linked server", r.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scrub_removes_comments_and_literals()
    {
        var scrubbed = SqlGuard.Scrub("SELECT 'DROP' /* DELETE */ -- UPDATE\n FROM t");
        Assert.DoesNotContain("DROP", scrubbed);
        Assert.DoesNotContain("DELETE", scrubbed);
        Assert.DoesNotContain("UPDATE", scrubbed);
        Assert.Contains("SELECT", scrubbed);
        Assert.Contains("FROM", scrubbed);
    }

    [Theory]
    // The model appends TOP when a row-count preference is in play, and T-SQL has no
    // trailing TOP. SQL Server's own message does not say where it belongs, so it
    // tries several equally wrong placements; the guard's message says exactly.
    [InlineData("SELECT domain, COUNT_BIG(*) AS n FROM dbo.PiholeQueries GROUP BY domain ORDER BY n DESC TOP (25)")]
    [InlineData("SELECT domain FROM dbo.PiholeQueries ORDER BY domain TOP 10")]
    [InlineData("SELECT domain, COUNT_BIG(*) n FROM dbo.PiholeQueries GROUP BY domain TOP (5)")]
    public void A_TOP_written_at_the_end_is_refused_with_the_correct_placement(string sql)
    {
        var verdict = SqlGuard.Validate(sql);

        Assert.False(verdict.Allowed);
        Assert.Contains("immediately after SELECT", verdict.Reason);
    }

    [Theory]
    // Correct placement must still pass, including without parentheses and with
    // DISTINCT between SELECT and TOP.
    [InlineData("SELECT TOP (25) domain, COUNT_BIG(*) AS n FROM dbo.PiholeQueries GROUP BY domain ORDER BY n DESC")]
    [InlineData("SELECT TOP 25 domain FROM dbo.PiholeQueries ORDER BY domain")]
    [InlineData("SELECT DISTINCT TOP (10) domain FROM dbo.PiholeQueries ORDER BY domain")]
    [InlineData("WITH v AS (SELECT TOP (10) domain FROM dbo.PiholeQueries ORDER BY domain) SELECT * FROM v")]
    public void A_correctly_placed_TOP_is_allowed(string sql)
    {
        Assert.True(SqlGuard.Validate(sql).Allowed, SqlGuard.Validate(sql).Reason);
    }

    [Fact]
    public void The_word_TOP_inside_a_string_literal_does_not_trip_the_check()
    {
        // Literals are scrubbed before the check, so a domain containing "top" after
        // an ORDER BY must not be refused.
        var verdict = SqlGuard.Validate(
            "SELECT TOP (10) domain FROM dbo.PiholeQueries ORDER BY CASE WHEN domain = 'desktop.example.com' THEN 0 ELSE 1 END");

        Assert.True(verdict.Allowed, verdict.Reason);
    }
}
