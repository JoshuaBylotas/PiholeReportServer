using Microsoft.Extensions.Caching.Memory;

namespace PiholeReportServer.Services;

public sealed record ClientEntry(string Ip, string? Hostname, string? Vendor)
{
    /// <summary>What the picker shows. Falls back to the IP when the device has no name.</summary>
    public string Label =>
        string.IsNullOrWhiteSpace(Hostname) ? Ip : $"{Hostname} ({Ip})";
}

/// <summary>
/// The list of known devices, for the builder's client picker.
/// <para>
/// Sourced from <c>dbo.DimClient</c>, which the dimension refresh rebuilds daily.
/// Deliberately not a <c>SELECT DISTINCT client</c> over the fact table: that is a scan
/// of tens of millions of rows to produce a couple of hundred values.
/// </para>
/// </summary>
public sealed class ClientDirectory
{
    private const string CacheKey = "client-directory";
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private const string Sql = """
        SELECT dc.ip, dc.hostname, dc.vendor
        FROM dbo.DimClient AS dc
        ORDER BY CASE WHEN dc.hostname IS NULL OR dc.hostname = '' THEN 1 ELSE 0 END,
                 dc.hostname,
                 dc.ip;
        """;

    private readonly ReportRunner _runner;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ClientDirectory> _log;

    public ClientDirectory(ReportRunner runner, IMemoryCache cache, ILogger<ClientDirectory> log)
    {
        _runner = runner;
        _cache = cache;
        _log = log;
    }

    /// <summary>
    /// Known devices, or an empty list when the dimension table is missing or empty —
    /// the caller shows a hint rather than failing the page.
    /// </summary>
    public async Task<IReadOnlyList<ClientEntry>> GetAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CacheKey, out IReadOnlyList<ClientEntry>? cached) && cached is not null)
        {
            return cached;
        }

        List<ClientEntry> entries = [];
        try
        {
            var result = await _runner.RunAsync(Sql, new Dictionary<string, object?>(), 10_000, ct);
            entries.AddRange(result.Rows
                .Where(r => r[0] is not null)
                .Select(r => new ClientEntry(
                    r[0]!.ToString()!,
                    r[1]?.ToString(),
                    r[2]?.ToString())));
        }
        catch (Exception ex)
        {
            // A missing DimClient must not take the builder down with it.
            _log.LogWarning(ex, "Client directory unavailable; the picker will be empty.");
            return entries;
        }

        _cache.Set(CacheKey, (IReadOnlyList<ClientEntry>)entries, Ttl);
        return entries;
    }
}
