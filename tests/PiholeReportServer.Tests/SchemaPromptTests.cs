using PiholeReportServer.Services;
using Xunit;

namespace PiholeReportServer.Tests;

/// <summary>
/// Guards the schema the model is given. These are not style checks: each one
/// corresponds to a column whose shape actively misleads a reader who has only
/// the column names, and the model has only the column names.
/// </summary>
public class SchemaPromptTests
{
    private static readonly string Schema = AiClient.SchemaForAgent;

    [Fact]
    public void Warns_that_num_queries_must_not_be_summed()
    {
        // DimClient holds one row per IP and FTL's per-device num_queries is copied
        // onto every one of them. SUM(num_queries) therefore multiplies the answer
        // by a device's address count - 175.7M against a true 76.5M here. Nothing
        // in the app sums it, but SUM is the obvious query for a model to write, so
        // the warning has to survive in the prompt.
        Assert.Contains("num_queries", Schema);
        Assert.Contains("NEVER SUM", Schema);
    }

    [Fact]
    public void States_that_DimClient_is_grained_by_ip_not_device()
    {
        // Counting rows to count devices overstates it; one host here has six rows.
        Assert.Contains("ONE ROW PER IP", Schema);
        Assert.Contains("GROUP BY dc.mac", Schema);
    }

    [Fact]
    public void Explains_that_a_reverse_dns_name_can_be_untrustworthy()
    {
        // 109 of 220 rows are flagged; grouping by name silently merges devices.
        Assert.Contains("name_ambiguous", Schema);
    }

    [Theory]
    [InlineData("dbo.PiholeQueries")]
    [InlineData("dbo.DimClient")]
    [InlineData("dbo.GravityDomains")]
    [InlineData("dbo.vDomainCategory")]
    [InlineData("dbo.DomainMetadata")]
    public void Describes_every_table_a_question_is_likely_to_need(string table)
    {
        Assert.Contains(table, Schema);
    }

    [Fact]
    public void Directs_category_queries_at_the_reconciled_view()
    {
        // The four corpora filling DomainCategory disagree on names - ut1 says
        // "ads", the rules say "advertising" - so filtering the raw table returns a
        // fraction of the matches while looking like a complete answer. The view
        // reconciles them, and the model must be pointed at it.
        Assert.Contains("canonical_category", Schema);
        Assert.Contains("never dbo.DomainCategory", Schema);
    }

    [Fact]
    public void Keeps_the_blocklist_fan_out_warning()
    {
        // GravityDomains has one row per (domain, adlist) pair, so a join inflates
        // counts. The 7B ignored this rule once and produced exactly that error.
        Assert.Contains("EXISTS", Schema);
    }

    [Fact]
    public void Still_forbids_everything_that_is_not_a_select()
    {
        foreach (var verb in new[] { "INSERT", "UPDATE", "DELETE", "DROP", "EXEC", "MERGE" })
        {
            Assert.Contains(verb, Schema);
        }
    }

    [Fact]
    public void Says_nothing_about_what_shape_a_reply_takes()
    {
        // This constant is spliced into two prompts with DIFFERENT reply contracts.
        // It carried the one-shot path's contract - Reply ONLY with JSON {"sql",
        // "notes"} - and the agent inherited it, so the agent's prompt asked for
        // {"action":…} first and then, later and more emphatically, for {"sql"}.
        // The model obeyed the second: it never emitted action:"answer", spent its
        // whole step budget querying, and every question ended with no answer and a
        // list of SQL. The schema describes the DATA; each caller states its own
        // contract.
        Assert.DoesNotContain("Reply ONLY", Schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"notes\"", Schema);
        Assert.DoesNotContain("Reply with", Schema, StringComparison.OrdinalIgnoreCase);
    }
}
