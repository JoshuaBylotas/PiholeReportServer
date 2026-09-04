using Microsoft.Extensions.Caching.Memory;

namespace PiholeReportServer.Services;

public sealed record AdlistEntry(int Id, string? Address, string? Comment, bool Enabled, long DomainCount)
{
    /// <summary>
    /// What the picker shows. Prefers the human comment ("Advertising"), falling back
    /// to a trimmed URL, because the raw addresses are long and near-identical.
    /// </summary>
    public string Label
    {
        get
        {
            var stem = !string.IsNullOrWhiteSpace(Comment)
                ? Comment!
                : ShortenAddress(Address) ?? $"list {Id}";

            var suffix = DomainCount > 0 ? $" — {DomainCount:N0} domains" : "";
            return Enabled ? $"{stem}{suffix}" : $"{stem}{suffix} (disabled)";
        }
    }

    private static string? ShortenAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        // Last path segment is the informative part of a blocklist URL.
        var trimmed = address.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 && slash < trimmed.Length - 1 ? trimmed[(slash + 1)..] : trimmed;
    }
}

/// <summary>
/// The subscribed blocklists, for the builder's blocklist picker.
/// <para>
/// The per-list domain counts come from <c>dbo.GravityDomains</c>, which has millions
/// of rows, so this is cached for longer than the client directory — the set of lists
/// changes when somebody edits Pi-hole's configuration, not minute to minute.
/// </para>
/// </summary>
public sealed class AdlistDirectory
{
    private const string CacheKey = "adlist-directory";
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    private const string Sql = """
        SELECT a.id,
               a.address,
               a.comment,
               ISNULL(a.enabled, 1) AS enabled,
               (SELECT COUNT_BIG(*) FROM dbo.GravityDomains AS g WHERE g.adlist_id = a.id) AS domains
        FROM dbo.Adlists AS a
        ORDER BY a.id;
        """;

    private readonly ReportRunner _runner;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AdlistDirectory> _log;

    public AdlistDirectory(ReportRunner runner, IMemoryCache cache, ILogger<AdlistDirectory> log)
    {
        _runner = runner;
        _cache = cache;
        _log = log;
    }

    public async Task<AdlistLookup> GetAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CacheKey, out AdlistLookup? cached) && cached is not null)
        {
            return cached;
        }

        List<AdlistEntry> entries = [];
        try
        {
            var result = await _runner.RunAsync(Sql, new Dictionary<string, object?>(), 1_000, ct);
            entries.AddRange(result.Rows
                .Where(r => r[0] is not null)
                .Select(r => new AdlistEntry(
                    Convert.ToInt32(r[0]),
                    r[1]?.ToString(),
                    r[2]?.ToString(),
                    r[3] is null || Convert.ToBoolean(r[3]),
                    r[4] is null ? 0 : Convert.ToInt64(r[4]))));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Adlist directory unavailable; the blocklist picker will be empty.");
            return new AdlistLookup([], ex.Message);
        }

        var lookup = new AdlistLookup(entries, null);
        _cache.Set(CacheKey, lookup, Ttl);
        return lookup;
    }
}

/// <summary>
/// The picker's contents plus, when the lookup failed, the reason — so an empty list
/// caused by a broken query is never presented as "there are no blocklists".
/// </summary>
public sealed record AdlistLookup(IReadOnlyList<AdlistEntry> Adlists, string? Error)
{
    public bool Failed => Error is not null;
}
