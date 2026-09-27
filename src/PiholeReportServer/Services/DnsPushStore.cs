using System.Data;
using System.Security.Claims;
using Microsoft.Data.SqlClient;
using PiholeReportServer.Data;
using PiholeReportServer.Models;

namespace PiholeReportServer.Services;

/// <summary>
/// Raises a DNS push request and reads back what the agent did with it.
/// <para>
/// This site never writes DNS. It inserts a row in <c>dbo.DnsPushRequest</c>
/// and the agent on WINAD02 - which already runs as a domain administrator, and
/// is the host the Omada controller lives on - claims it, performs the record
/// changes, and writes the outcome to <c>dbo.DnsPushLog</c>.
/// </para>
/// <para>
/// The alternative was granting the application pool write rights on the zone,
/// which would make a web application a DNS administrator. A queue costs a few
/// seconds of latency and buys an audit trail, a dry run, and an app that
/// cannot change name resolution even if it is compromised.
/// </para>
/// </summary>
public sealed class DnsPushStore
{
    /// <summary>
    /// A request already waiting means the agent has not run yet. Queueing a
    /// second identical one would make it act twice for no reason, so the page
    /// is told to wait instead.
    /// </summary>
    public const int MaxQueuedAtOnce = 3;

    private readonly ISqlConnectionFactory _factory;
    private readonly ILogger<DnsPushStore> _log;

    public DnsPushStore(ISqlConnectionFactory factory, ILogger<DnsPushStore> log)
    {
        _factory = factory;
        _log = log;
    }

    private static SqlCommand Command(SqlConnection conn, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandType = CommandType.Text;
        return cmd;
    }

    /// <summary>
    /// Queues a push. <paramref name="mac"/> null means every publishable
    /// device; otherwise just that one.
    /// </summary>
    /// <returns>The new request id, or null when too many are already waiting.</returns>
    public async Task<int?> QueueAsync(
        string? mac, bool dryRun, ClaimsPrincipal user, CancellationToken ct = default)
    {
        var canonical = mac is null ? null : DeviceTruthStore.NormaliseMac(mac);
        if (mac is not null && canonical is null)
        {
            throw new ArgumentException($"Not a MAC address: {mac}", nameof(mac));
        }

        var oid = DeviceTruthStore.EditorOid(user)
                  ?? throw new InvalidOperationException(
                      "No object id claim on the signed-in principal; cannot attribute the request.");

        const string sql = """
            SET NOCOUNT ON;
            IF (SELECT COUNT(*) FROM dbo.DnsPushRequest
                WHERE status IN ('queued', 'running')) >= @cap
            BEGIN
                SELECT CAST(NULL AS int);
            END
            ELSE
            BEGIN
                INSERT INTO dbo.DnsPushRequest
                    (mac, dry_run, status, requested_by_oid, requested_by_name, requested_utc)
                VALUES (@mac, @dry, 'queued', @oid, @who, SYSUTCDATETIME());
                SELECT CAST(SCOPE_IDENTITY() AS int);
            END
            """;

        await using var conn = await _factory.OpenWriteAsync(ct);
        await using var cmd = Command(conn, sql);
        cmd.Parameters.Add(new SqlParameter("@mac", (object?)canonical ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@dry", dryRun));
        cmd.Parameters.Add(new SqlParameter("@oid", oid));
        cmd.Parameters.Add(new SqlParameter("@who",
            (object?)DeviceTruthStore.EditorName(user) ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@cap", MaxQueuedAtOnce));

        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is null or DBNull)
        {
            _log.LogWarning("DNS push not queued: {Cap} already waiting", MaxQueuedAtOnce);
            return null;
        }

        var id = Convert.ToInt32(result);
        _log.LogInformation(
            "DNS push queued: id={Id} scope={Scope} dryRun={DryRun} by {Who}",
            id, canonical ?? "all", dryRun,
            DeviceTruthStore.EditorName(user) ?? oid);
        return id;
    }

    /// <summary>The most recent requests, newest first.</summary>
    public async Task<IReadOnlyList<DnsPushRequestRow>> RecentAsync(
        int take = 10, CancellationToken ct = default)
    {
        const string sql = """
            SELECT TOP (@take)
                   id, mac, dry_run, status, requested_by_name, requested_utc,
                   claimed_utc, completed_utc, agent_host,
                   records_changed, records_failed, message
            FROM dbo.DnsPushRequest
            ORDER BY id DESC;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        await using var cmd = Command(conn, sql);
        cmd.Parameters.Add(new SqlParameter("@take", take));

        var rows = new List<DnsPushRequestRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            rows.Add(new DnsPushRequestRow
            {
                Id = r.GetInt32(0),
                Mac = r.IsDBNull(1) ? null : r.GetString(1),
                DryRun = r.GetBoolean(2),
                Status = r.GetString(3),
                RequestedByName = r.IsDBNull(4) ? null : r.GetString(4),
                RequestedUtc = r.GetDateTime(5),
                ClaimedUtc = r.IsDBNull(6) ? null : r.GetDateTime(6),
                CompletedUtc = r.IsDBNull(7) ? null : r.GetDateTime(7),
                AgentHost = r.IsDBNull(8) ? null : r.GetString(8),
                RecordsChanged = r.IsDBNull(9) ? null : r.GetInt32(9),
                RecordsFailed = r.IsDBNull(10) ? null : r.GetInt32(10),
                Message = r.IsDBNull(11) ? null : r.GetString(11),
            });
        }
        return rows;
    }

    /// <summary>
    /// What the agent did for one request, oldest first - which is the order it
    /// happened in.
    /// </summary>
    public async Task<IReadOnlyList<DnsPushLogRow>> LogAsync(
        int requestId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT id, request_id, mac, action, record_type,
                   record_name, ip, zone, detail, acted_utc
            FROM dbo.DnsPushLog
            WHERE request_id = @id
            ORDER BY id;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        await using var cmd = Command(conn, sql);
        cmd.Parameters.Add(new SqlParameter("@id", requestId));

        var rows = new List<DnsPushLogRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            rows.Add(new DnsPushLogRow
            {
                Id = r.GetInt64(0),
                RequestId = r.GetInt32(1),
                Mac = r.IsDBNull(2) ? null : r.GetString(2),
                Action = r.GetString(3),
                RecordType = r.GetString(4),
                RecordName = r.IsDBNull(5) ? null : r.GetString(5),
                Ip = r.IsDBNull(6) ? null : r.GetString(6),
                Zone = r.IsDBNull(7) ? null : r.GetString(7),
                Detail = r.IsDBNull(8) ? null : r.GetString(8),
                ActedUtc = r.GetDateTime(9),
            });
        }
        return rows;
    }
}
