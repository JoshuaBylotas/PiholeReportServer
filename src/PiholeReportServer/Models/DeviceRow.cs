namespace PiholeReportServer.Models;

/// <summary>
/// One name offered by one source, for the editor to show as a choice.
/// </summary>
/// <param name="Source">
/// 'omada', 'mdns', 'ssdp', 'addns'... The source is shown next to the name
/// because which source said it is most of what makes it trustworthy.
/// </param>
public sealed record DeviceCandidate(
    string Source,
    string Name,
    string? Ip,
    DateTime ObservedUtc);

/// <summary>
/// A row of <c>dbo.vDeviceTruth</c>: what a device is called, where it is, and
/// who says so - plus every name any source offered, so the editor is a choice
/// between the observed options rather than a blank box.
/// </summary>
public sealed class DeviceRow
{
    public required string Mac { get; init; }

    /// <summary>
    /// The resolved name. Null when no source has a usable one - which is the
    /// case the editor exists to fix.
    /// </summary>
    public string? DeviceName { get; init; }

    public string? CurrentIp { get; init; }

    /// <summary>'manual' when a person stated it, otherwise the winning source, or 'none'.</summary>
    public required string NameSource { get; init; }

    public string? IpSource { get; init; }
    public DateTime? IpObservedUtc { get; init; }

    public string? Vendor { get; init; }
    public string? DeviceType { get; init; }

    /// <summary>Whether the push agent should publish this device to DNS.</summary>
    public bool PublishDns { get; init; }

    public string? Notes { get; init; }
    public string? StatedBy { get; init; }
    public DateTime? StatedUtc { get; init; }

    /// <summary>What AD DNS said as of the last addns collection, for reconciliation.</summary>
    public string? AdDnsName { get; init; }
    public string? AdDnsIp { get; init; }

    /// <summary>
    /// Null when AD DNS has nothing for this MAC. False is the interesting case:
    /// the zone and this table disagree about the name or the address.
    /// </summary>
    public bool? AdDnsAgrees { get; init; }

    public int CandidateNames { get; init; }

    /// <summary>
    /// No stated name, and the best any source offered was the device talking
    /// about itself. This is the editor's worklist.
    /// </summary>
    public bool NeedsReview { get; init; }

    /// <summary>Set only on the detail path; empty on the list.</summary>
    public IReadOnlyList<DeviceCandidate> Candidates { get; init; } = [];

    /// <summary>True when a person has stated this name rather than inferred it.</summary>
    public bool IsStated => string.Equals(NameSource, "manual", StringComparison.Ordinal);
}

/// <summary>A queued, running or finished request for the WINAD02 agent.</summary>
public sealed class DnsPushRequestRow
{
    public int Id { get; init; }
    public string? Mac { get; init; }
    public bool DryRun { get; init; }
    public required string Status { get; init; }
    public string? RequestedByName { get; init; }
    public DateTime RequestedUtc { get; init; }
    public DateTime? ClaimedUtc { get; init; }
    public DateTime? CompletedUtc { get; init; }
    public string? AgentHost { get; init; }
    public int? RecordsChanged { get; init; }
    public int? RecordsFailed { get; init; }
    public string? Message { get; init; }

    public bool IsFinished => Status is "done" or "failed";
}

/// <summary>One DNS record the agent added, updated, removed, skipped or failed.</summary>
public sealed class DnsPushLogRow
{
    public long Id { get; init; }
    public int RequestId { get; init; }
    public string? Mac { get; init; }
    public required string Action { get; init; }
    public required string RecordType { get; init; }
    public string? RecordName { get; init; }
    public string? Ip { get; init; }
    public string? Zone { get; init; }
    public string? Detail { get; init; }
    public DateTime ActedUtc { get; init; }
}
