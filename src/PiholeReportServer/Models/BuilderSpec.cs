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

    [Display(Name = "Domain contains")]
    [StringLength(255)]
    public string? DomainFilter { get; set; }

    [Display(Name = "Status")]
    public int? StatusFilter { get; set; }

    [Display(Name = "Query type")]
    public int? TypeFilter { get; set; }

    [Display(Name = "Only domains on a blocklist")]
    public bool OnlyBlocklisted { get; set; }

    [Display(Name = "Sort")]
    public SortDirection Sort { get; set; } = SortDirection.Desc;

    [Display(Name = "Row limit")]
    [Range(1, 100_000)]
    public int Limit { get; set; } = 200;
}
