using System.ComponentModel.DataAnnotations;

namespace PiholeReportServer.Models;

/// <summary>
/// The guided report builder. Every field maps to a whitelisted SQL fragment —
/// no user text ever reaches the query except as a bound parameter, so the
/// builder is safe for any signed-in viewer.
/// </summary>
public sealed class BuilderSpec
{
    public enum GroupDimension
    {
        None,
        Domain,
        Client,
        ClientHostname,
        QueryType,
        Status,
        Upstream,
        Blocklist,
        Hour,
        Day,
        Week,
    }

    public enum MetricKind
    {
        QueryCount,
        DistinctDomains,
        DistinctClients,
        AvgReplyMs,
        MaxReplyMs,
    }

    public enum SortDirection
    {
        Desc,
        Asc,
    }

    [Display(Name = "From")]
    public DateTime? From { get; set; }

    [Display(Name = "To")]
    public DateTime? To { get; set; }

    [Display(Name = "Group by")]
    public GroupDimension GroupBy { get; set; } = GroupDimension.Domain;

    [Display(Name = "Then by")]
    public GroupDimension ThenBy { get; set; } = GroupDimension.None;

    [Display(Name = "Metric")]
    public MetricKind Metric { get; set; } = MetricKind.QueryCount;

    [Display(Name = "Client IP contains")]
    [StringLength(64)]
    public string? ClientFilter { get; set; }

    /// <summary>
    /// Clients chosen from the multi-select picker. The picker shows device
    /// names, but its option values are IPs: that is what the fact table
    /// stores and what is indexed, and one host name can cover more than one
    /// address. Empty means "all clients".
    /// </summary>
    [Display(Name = "Clients")]
    public List<string> ClientIps { get; set; } = [];

    [Display(Name = "Domain contains")]
    [StringLength(255)]
    public string? DomainFilter { get; set; }

    [Display(Name = "Status")]
    public int? StatusFilter { get; set; }

    [Display(Name = "Query type")]
    public int? TypeFilter { get; set; }

    /// <summary>
    /// Blocklists to restrict to, by <c>dbo.Adlists.id</c>. Empty means no blocklist
    /// filter at all; <see cref="AnyBlocklistId"/> means "on any subscribed list",
    /// which is what the old single checkbox used to mean.
    /// </summary>
    [Display(Name = "On these blocklists")]
    public List<int> BlocklistIds { get; set; } = [];

    /// <summary>Sentinel option meaning "any subscribed blocklist".</summary>
    public const int AnyBlocklistId = -1;

    [Display(Name = "Sort")]
    public SortDirection Sort { get; set; } = SortDirection.Desc;

    [Display(Name = "Row limit")]
    [Range(1, 100_000)]
    public int Limit { get; set; } = 200;
}
