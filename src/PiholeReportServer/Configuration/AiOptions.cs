namespace PiholeReportServer.Configuration;

/// <summary>
/// The local inference endpoint used for natural-language querying and result
/// summaries.
/// <para>
/// Deliberately a machine on the LAN rather than a hosted API: DNS query history is
/// a detailed record of household behaviour, and this way none of it leaves the
/// network.
/// </para>
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "Ai";

    /// <summary>When false the AI controls are hidden and the endpoints refuse.</summary>
    public bool Enabled { get; set; }

    /// <summary>Base address of the Ollama server, e.g. http://10.0.0.5:11434.</summary>
    public string Endpoint { get; set; } = "";

    public string Model { get; set; } = "qwen2.5-coder:3b";

    /// <summary>
    /// Generous by default: a CPU-only host produces a few tokens per second, so a
    /// question can legitimately take the better part of a minute.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// Hard ceiling on rows sent for a prose summary. Summarising is the expensive
    /// direction — every row becomes tokens — so this stays small regardless of how
    /// large the result set is.
    /// </summary>
    public int MaxSummaryRows { get; set; } = 200;

    /// <summary>Low, because the useful output here is a correct query, not a creative one.</summary>
    public double Temperature { get; set; } = 0.1;

    /// <summary>Upper bound on generated tokens, so a runaway response cannot hang the page.</summary>
    public int MaxOutputTokens { get; set; } = 800;
}
