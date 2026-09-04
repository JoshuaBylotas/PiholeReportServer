using Microsoft.Extensions.Caching.Memory;

namespace PiholeReportServer.Services;

public sealed record ClientEntry(string Ip, string? Name, string? MacVendor)
{
    /// <summary>What the picker shows. Falls back to the IP when the device has no name.</summary>
    public string Label =>
        string.IsNullOrWhiteSpace(Name) ? Ip : $"{Name} ({Ip})";
}

/// <summary>
/// The picker's contents plus, when the lookup failed, the reason. An empty list with
/// no error genuinely means "no devices"; an empty list with an error means something
/// is wrong and the message says what.
/// </summary>
public sealed record ClientLookup(IReadOnlyList<ClientEntry> Clients, string? Error)
{
    public bool Failed => Error is not null;
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
        SELECT dc.ip, dc.name, dc.mac_vendor
        FROM dbo.DimClient AS dc
        ORDER BY CASE WHEN dc.name IS NULL OR dc.name = '' THEN 1 ELSE 0 END,
                 dc.name,
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
    /// Known devices. A failure is reported in <see cref="ClientLookup.Error"/> rather
    /// than thrown, so the builder still renders — but the reason is surfaced, not
    /// swallowed. Reporting a genuine SQL error as "no devices" once cost real
    /// debugging time: an invalid column name looked identical to an empty table.
    /// </summary>
    public async Task<ClientLookup> GetAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CacheKey, out ClientLookup? cached) && cached is not null)
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
            // A broken DimClient must not take the builder down with it, but the
            // caller needs to know *why* the picker is empty.
            _log.LogWarning(ex, "Client directory unavailable; the picker will be empty.");
            return new ClientLookup([], ex.Message);
        }

        var lookup = new ClientLookup(entries, null);
        _cache.Set(CacheKey, lookup, Ttl);
        return lookup;
    }
}
