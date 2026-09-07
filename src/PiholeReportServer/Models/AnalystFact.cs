namespace PiholeReportServer.Models;

/// <summary>
/// One standing fact the Analyst has been told, from <c>dbo.AnalystMemory</c>.
/// <para>
/// The warehouse knows hostnames; people know "Jason's phone". A fact bridges the two
/// so a question can be asked the way it is actually thought.
/// </para>
/// </summary>
public sealed class AnalystFact
{
    public int Id { get; init; }

    /// <summary>"device" for an alias that becomes a filter, "note" for anything else.</summary>
    public required string Kind { get; init; }

    /// <summary>What the owner calls it, e.g. "Jason's phone". Unique per owner.</summary>
    public required string Subject { get; init; }

    /// <summary>What the network calls it: a hostname, IP or MAC. Null for a note.</summary>
    public string? Target { get; init; }

    /// <summary>The sentence given to the model.</summary>
    public required string Fact { get; init; }

    public DateTime CreatedUtc { get; init; }

    public DateTime UpdatedUtc { get; init; }

    /// <summary>Prompts that have carried this fact, so a dead one can be spotted.</summary>
    public int UsedCount { get; init; }

    public bool IsDevice => Kind == "device";
}
