namespace PiholeReportServer.Models;

/// <summary>
/// What the shared AI panel needs to render. Built by each page that shows results, so
/// the panel itself stays free of page-specific knowledge.
/// </summary>
public sealed class AiPanelViewModel
{
    /// <summary>False hides the panel entirely (Ai:Enabled off, or no endpoint).</summary>
    public required bool Enabled { get; init; }

    /// <summary>
    /// Whether this user may ask free-text questions. Natural-language querying
    /// generates arbitrary SQL, so it carries the same role requirement as the
    /// console rather than being open to every viewer.
    /// </summary>
    public required bool CanAsk { get; init; }

    /// <summary>
    /// Pre-computed statistical summary of the visible result, ~1 KB. Computed when
    /// the page renders so that "explain" costs one small POST rather than shipping
    /// thousands of rows back to the server.
    /// </summary>
    public string? Digest { get; init; }

    /// <summary>Short human description of what produced the result, e.g. "Top domains".</summary>
    public string Context { get; init; } = "report";

    /// <summary>Model name, shown so it is obvious which engine answered.</summary>
    public string? Model { get; init; }

    public bool HasResult => !string.IsNullOrWhiteSpace(Digest);

    /// <summary>
    /// Builds the panel state for a page. One helper rather than the same six lines on
    /// each of the three pages that show results.
    /// </summary>
    public static AiPanelViewModel For(
        bool aiEnabled,
        string? model,
        bool canAsk,
        QueryResult? result,
        string context) => new()
        {
            Enabled = aiEnabled,
            CanAsk = canAsk,
            Model = model,
            Context = context,
            // Built here, at render time, so "explain" is one small POST rather than
            // shipping thousands of rows back to the server.
            Digest = result is null ? null : Services.ResultDigest.Build(result, context),
        };
}
