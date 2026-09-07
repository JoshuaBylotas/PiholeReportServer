using Microsoft.Extensions.Caching.Memory;
using PiholeReportServer.Models;

namespace PiholeReportServer.Services;

/// <summary>
/// Holds the running conversations for the Analyst page.
/// <para>
/// The page used to be one question in, one prose answer out, which made it behave
/// like a query generator rather than something you can talk to: there was no way to
/// say "no, the list" or "same thing for last month" without starting again. Keeping
/// the turns lets the model resolve "that device" itself.
/// </para>
/// <para>
/// In memory and per user, with a sliding expiry. A conversation is a convenience,
/// not a record — nothing here needs to survive a recycle, and DNS history is
/// sensitive enough that not persisting it is the better default.
/// </para>
/// </summary>
public sealed class ConversationStore : IDisposable
{
    /// <summary>
    /// Turns kept and replayed to the model. Each turn carries the assistant's own
    /// JSON replies and the row digests it was shown, so this is the real cost driver
    /// on prompt length; six is enough for "and now just for that device" to work
    /// several times over.
    /// </summary>
    public const int MaxTurns = 6;

    /// <summary>Conversations held at once, across all users.</summary>
    private const int Capacity = 200;

    private static readonly TimeSpan Idle = TimeSpan.FromHours(2);

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = Capacity });
    private readonly ILogger<ConversationStore> _log;

    public ConversationStore(ILogger<ConversationStore> log) => _log = log;

    /// <summary>
    /// Fetches a conversation, or starts one. Returns null when the id belongs to
    /// someone else — the id travels via the browser, so ownership is checked here
    /// rather than trusted.
    /// </summary>
    public Conversation? GetOrStart(string ownerOid, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return Start(ownerOid);
        }

        if (!_cache.TryGetValue(id, out Conversation? found) || found is null)
        {
            // Expired or unknown: start a fresh one rather than failing. Losing
            // history is a mild annoyance; refusing to answer is not.
            return Start(ownerOid);
        }

        if (!string.Equals(found.OwnerOid, ownerOid, StringComparison.Ordinal))
        {
            _log.LogWarning("Conversation {Id} requested by a different user.", id);
            return null;
        }

        return found;
    }

    private Conversation Start(string ownerOid)
    {
        var convo = new Conversation
        {
            Id = Guid.NewGuid().ToString("N"),
            OwnerOid = ownerOid,
        };
        Save(convo);
        return convo;
    }

    /// <summary>Writes a conversation back and refreshes its idle expiry.</summary>
    public void Save(Conversation convo)
    {
        // Trim before storing: an unbounded history would grow the prompt until it
        // crowded out the schema, and a model that cannot see the schema writes
        // queries against columns that do not exist.
        while (convo.Turns.Count > MaxTurns)
        {
            convo.Turns.RemoveAt(0);
        }

        _cache.Set(convo.Id, convo, new MemoryCacheEntryOptions
        {
            Size = 1,
            SlidingExpiration = Idle,
        });
    }

    public void Clear(string ownerOid, string id)
    {
        if (_cache.TryGetValue(id, out Conversation? found) && found is not null &&
            string.Equals(found.OwnerOid, ownerOid, StringComparison.Ordinal))
        {
            _cache.Remove(id);
        }
    }

    public void Dispose() => _cache.Dispose();
}
