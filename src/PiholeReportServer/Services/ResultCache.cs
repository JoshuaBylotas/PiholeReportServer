using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PiholeReportServer.Configuration;
using PiholeReportServer.Models;

namespace PiholeReportServer.Services;

/// <summary>
/// Holds a materialised result set so it can be paged and searched without re-running
/// the query.
/// <para>
/// This exists because the row cap moved from 5,000 to 100,000. Rendering that many
/// rows into the DOM is not viable — it is roughly 100 MB of markup and seconds of
/// browser freeze — and paging server-side the naive way would mean re-running an
/// aggregate over 24M rows to fetch the next hundred. So the query runs once, the
/// rows live here, and paging is a slice of an in-memory list.
/// </para>
/// <para>
/// Its own <see cref="MemoryCache"/> instance rather than the shared one, because it
/// needs a hard <c>SizeLimit</c>: 100,000 rows is tens of megabytes, and a handful of
/// concurrent users could otherwise exhaust the worker process. Entries are sized in
/// rows and evicted least-recently-used.
/// </para>
/// </summary>
public sealed class ResultCache : IDisposable
{
    /// <summary>
    /// Total rows held across all cached results. At roughly 400 bytes per row this
    /// bounds the cache to a few hundred megabytes regardless of how many people are
    /// running large queries.
    /// </summary>
    private const long TotalRowBudget = 600_000;

    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(20);

    private readonly MemoryCache _cache;
    private readonly ILogger<ResultCache> _log;
    private readonly ReportingOptions _reporting;

    public ResultCache(IOptions<ReportingOptions> reporting, ILogger<ResultCache> log)
    {
        _reporting = reporting.Value;
        _log = log;
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = TotalRowBudget,
            // Evict a little eagerly, so a big new result does not have to wait on
            // the periodic scan to make room.
            CompactionPercentage = 0.25,
        });
    }

    public int PageSize => Math.Clamp(_reporting.PageSize, 10, 1000);

    /// <summary>
    /// Stores a result and returns the token used to page through it. The token is
    /// unguessable and the entry records its owner, so one user cannot read another's
    /// cached rows by holding on to a token.
    /// </summary>
    public string Store(string ownerOid, QueryResult result, string description)
    {
        var token = Guid.NewGuid().ToString("N");
        var entry = new CachedResult(ownerOid, result, description, DateTime.UtcNow);

        _cache.Set(token, entry, new MemoryCacheEntryOptions
        {
            SlidingExpiration = Ttl,
            // Sized in rows; at minimum 1 so an empty result still occupies a slot.
            Size = Math.Max(1, result.Rows.Count),
        });

        _log.LogDebug("Cached {Rows} rows as {Token} for {Owner}.",
            result.Rows.Count, token, ownerOid);
        return token;
    }

    /// <summary>
    /// Returns one page, applying the search term first. Null when the token has
    /// expired or belongs to somebody else — the caller treats both the same, since
    /// distinguishing them would leak whether a token exists.
    /// </summary>
    public ResultPage? GetPage(string ownerOid, string token, int page, string? search)
    {
        if (!_cache.TryGetValue(token, out CachedResult? entry) || entry is null)
        {
            return null;
        }
        if (!string.Equals(entry.OwnerOid, ownerOid, StringComparison.Ordinal))
        {
            _log.LogWarning("Rejected a cached-result token presented by a different user.");
            return null;
        }

        var matching = entry.Match(search);
        var size = PageSize;
        var pageCount = Math.Max(1, (int)Math.Ceiling(matching.Count / (double)size));
        var index = Math.Clamp(page, 0, pageCount - 1);
        var start = index * size;
        var take = Math.Min(size, Math.Max(0, matching.Count - start));

        return new ResultPage
        {
            Token = token,
            Columns = entry.Result.Columns,
            Rows = matching.Skip(start).Take(take).Select(i => entry.Result.Rows[i]).ToList(),
            Page = index,
            PageCount = pageCount,
            PageSize = size,
            MatchingRows = matching.Count,
            TotalRows = entry.Result.Rows.Count,
            Truncated = entry.Result.Truncated,
            RowCap = entry.Result.RowCap,
            Search = search,
        };
    }

    public void Dispose() => _cache.Dispose();

    /// <summary>
    /// One cached result, with a lazily built lowercase index so searching does not
    /// re-stringify every row on each keystroke.
    /// </summary>
    private sealed class CachedResult(
        string ownerOid, QueryResult result, string description, DateTime storedUtc)
    {
        private readonly Lock _gate = new();
        private string[]? _haystack;

        public string OwnerOid { get; } = ownerOid;
        public QueryResult Result { get; } = result;
        public string Description { get; } = description;
        public DateTime StoredUtc { get; } = storedUtc;

        /// <summary>
        /// Row indices matching the search. Space-separated terms must all appear, so
        /// a second word narrows rather than widens.
        /// </summary>
        public IReadOnlyList<int> Match(string? search)
        {
            var term = search?.Trim();
            if (string.IsNullOrEmpty(term))
            {
                return AllIndices();
            }

            var terms = term.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var hay = Haystack();
            var hits = new List<int>();

            for (var i = 0; i < hay.Length; i++)
            {
                var row = hay[i];
                var all = true;
                foreach (var t in terms)
                {
                    if (!row.Contains(t, StringComparison.Ordinal))
                    {
                        all = false;
                        break;
                    }
                }
                if (all)
                {
                    hits.Add(i);
                }
            }
            return hits;
        }

        private int[] AllIndices()
        {
            var all = new int[Result.Rows.Count];
            for (var i = 0; i < all.Length; i++)
            {
                all[i] = i;
            }
            return all;
        }

        private string[] Haystack()
        {
            if (_haystack is not null)
            {
                return _haystack;
            }
            lock (_gate)
            {
                if (_haystack is not null)
                {
                    return _haystack;
                }

                // Built on first search rather than at store time: most results are
                // paged through without ever being searched, and this doubles the
                // memory an entry occupies.
                var built = new string[Result.Rows.Count];
                for (var i = 0; i < built.Length; i++)
                {
                    built[i] = string.Join('', Result.Rows[i].Select(Text)).ToLowerInvariant();
                }
                _haystack = built;
                return built;
            }
        }

        private static string Text(object? v) => v switch
        {
            null => "",
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString() ?? "",
        };
    }
}
