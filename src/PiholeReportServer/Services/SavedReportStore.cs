using System.Data;
using System.Security.Claims;
using Microsoft.Data.SqlClient;
using PiholeReportServer.Data;
using PiholeReportServer.Models;

namespace PiholeReportServer.Services;

/// <summary>
/// Persists per-user saved reports in <c>dbo.SavedReports</c>.
/// <para>
/// Every statement filters on <c>owner_oid</c>, and that value always comes from the
/// signed-in principal's <c>oid</c> claim — never from the request. A user therefore
/// cannot read, overwrite or delete somebody else's report by guessing an id.
/// </para>
/// <para>
/// This is the only table the application writes to; the warehouse remains read-only
/// to the reporting login, which is what the SQL console's safety argument rests on.
/// </para>
/// </summary>
public sealed class SavedReportStore
{
    /// <summary>Ceiling per user, so the shared table cannot be filled by one account.</summary>
    public const int MaxPerUser = 200;

    public const int MaxPayloadChars = 100_000;

    private readonly ISqlConnectionFactory _factory;
    private readonly ILogger<SavedReportStore> _log;

    public SavedReportStore(ISqlConnectionFactory factory, ILogger<SavedReportStore> log)
    {
        _factory = factory;
        _log = log;
    }

    /// <summary>
    /// The stable Entra object id for a principal. Falls back to the standard
    /// name-identifier claim, then the UPN, so the store still works in an unusual
    /// token configuration rather than silently losing everyone's reports.
    /// </summary>
    public static string? OwnerOid(ClaimsPrincipal user) =>
        user.FindFirst("oid")?.Value
        ?? user.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
        ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? user.FindFirst("preferred_username")?.Value;

    public static string? OwnerName(ClaimsPrincipal user) =>
        user.FindFirst("name")?.Value
        ?? user.FindFirst("preferred_username")?.Value
        ?? user.Identity?.Name;

    public async Task<IReadOnlyList<SavedReport>> ListAsync(
        string ownerOid, SavedReportKind? kind = null, CancellationToken ct = default)
    {
        const string sql = """
            SELECT id, owner_oid, owner_name, name, description, kind, payload,
                   created_utc, updated_utc, run_count, last_run_utc
            FROM dbo.SavedReports
            WHERE owner_oid = @owner
              AND (@kind IS NULL OR kind = @kind)
            ORDER BY name;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        await using var cmd = Command(conn, sql);
        cmd.Parameters.Add(new SqlParameter("@owner", ownerOid));
        cmd.Parameters.Add(new SqlParameter("@kind", (object?)kind?.ToString() ?? DBNull.Value));

        var list = new List<SavedReport>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(Map(r));
        }
        return list;
    }

    public async Task<SavedReport?> GetAsync(string ownerOid, int id, CancellationToken ct = default)
    {
        const string sql = """
            SELECT id, owner_oid, owner_name, name, description, kind, payload,
                   created_utc, updated_utc, run_count, last_run_utc
            FROM dbo.SavedReports
            WHERE owner_oid = @owner AND id = @id;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        await using var cmd = Command(conn, sql);
        cmd.Parameters.Add(new SqlParameter("@owner", ownerOid));
        cmd.Parameters.Add(new SqlParameter("@id", id));

        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    /// <summary>
    /// Creates the report, or updates it when the user already has one by that name.
    /// Returns its id.
    /// </summary>
    public async Task<int> SaveAsync(
        string ownerOid,
        string? ownerName,
        string name,
        string? description,
        SavedReportKind kind,
        string payload,
        CancellationToken ct = default)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("A saved report needs a name.", nameof(name));
        }
        if (payload.Length > MaxPayloadChars)
        {
            throw new ArgumentException(
                $"That report is too large to save ({payload.Length:N0} characters; the limit is {MaxPayloadChars:N0}).",
                nameof(payload));
        }

        // MERGE is avoided deliberately: it has well-known concurrency edge cases, and
        // the unique constraint on (owner_oid, name) already makes "update if present,
        // else insert" safe when written as two statements in one batch.
        const string sql = """
            SET NOCOUNT ON;

            UPDATE dbo.SavedReports
            SET description = @description,
                kind        = @kind,
                payload     = @payload,
                owner_name  = @ownerName,
                updated_utc = SYSUTCDATETIME()
            WHERE owner_oid = @owner AND name = @name;

            IF @@ROWCOUNT = 0
            BEGIN
                IF (SELECT COUNT(*) FROM dbo.SavedReports WHERE owner_oid = @owner) >= @maxPerUser
                    THROW 50001, 'Saved report limit reached for this user.', 1;

                INSERT INTO dbo.SavedReports (owner_oid, owner_name, name, description, kind, payload)
                VALUES (@owner, @ownerName, @name, @description, @kind, @payload);
            END

            SELECT id FROM dbo.SavedReports WHERE owner_oid = @owner AND name = @name;
            """;

        await using var conn = await _factory.OpenWriteAsync(ct);
        await using var cmd = Command(conn, sql);
        cmd.Parameters.Add(new SqlParameter("@owner", ownerOid));
        cmd.Parameters.Add(new SqlParameter("@ownerName", (object?)ownerName ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@name", name));
        cmd.Parameters.Add(new SqlParameter("@description", (object?)description ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@kind", kind.ToString()));
        cmd.Parameters.Add(new SqlParameter("@payload", payload));
        cmd.Parameters.Add(new SqlParameter("@maxPerUser", MaxPerUser));

        var id = await cmd.ExecuteScalarAsync(ct);
        _log.LogInformation("Saved report '{Name}' ({Kind}) for {Owner}.", name, kind, ownerOid);
        return Convert.ToInt32(id);
    }

    public async Task<bool> DeleteAsync(string ownerOid, int id, CancellationToken ct = default)
    {
        const string sql = "DELETE FROM dbo.SavedReports WHERE owner_oid = @owner AND id = @id;";

        await using var conn = await _factory.OpenWriteAsync(ct);
        await using var cmd = Command(conn, sql);
        cmd.Parameters.Add(new SqlParameter("@owner", ownerOid));
        cmd.Parameters.Add(new SqlParameter("@id", id));

        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return affected > 0;
    }

    /// <summary>
    /// Bumps the run counters. Best-effort: a failure here must never stop a report
    /// the user asked for from being displayed.
    /// </summary>
    public async Task TouchAsync(string ownerOid, int id, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE dbo.SavedReports
            SET run_count = run_count + 1, last_run_utc = SYSUTCDATETIME()
            WHERE owner_oid = @owner AND id = @id;
            """;

        try
        {
            await using var conn = await _factory.OpenWriteAsync(ct);
            await using var cmd = Command(conn, sql);
            cmd.Parameters.Add(new SqlParameter("@owner", ownerOid));
            cmd.Parameters.Add(new SqlParameter("@id", id));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not update run counters for saved report {Id}.", id);
        }
    }

    private static SqlCommand Command(SqlConnection conn, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandType = CommandType.Text;
        return cmd;
    }

    private static SavedReport Map(SqlDataReader r) => new()
    {
        Id = r.GetInt32(0),
        OwnerOid = r.GetString(1),
        OwnerName = r.IsDBNull(2) ? null : r.GetString(2),
        Name = r.GetString(3),
        Description = r.IsDBNull(4) ? null : r.GetString(4),
        Kind = Enum.TryParse<SavedReportKind>(r.GetString(5), out var k) ? k : SavedReportKind.Builder,
        Payload = r.GetString(6),
        CreatedUtc = r.GetDateTime(7),
        UpdatedUtc = r.GetDateTime(8),
        RunCount = r.GetInt32(9),
        LastRunUtc = r.IsDBNull(10) ? null : r.GetDateTime(10),
    };
}
