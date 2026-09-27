using System.Data;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using PiholeReportServer.Data;
using PiholeReportServer.Models;

namespace PiholeReportServer.Services;

/// <summary>
/// Reads <c>dbo.vDeviceTruth</c> and writes the one thing in it a person owns:
/// the stated name in <c>dbo.DeviceTruth</c>.
/// <para>
/// The stated name lives in its own table rather than as a 'manual' row in
/// <c>dbo.DeviceNameObservation</c>, because the collectors replace their source
/// wholesale on every run. A typed name in an observation table would survive
/// until the next collection and then vanish, which is the worst possible
/// behaviour for the only authoritative record on the network.
/// </para>
/// </summary>
public sealed partial class DeviceTruthStore
{
    /// <summary>A DNS label: letters, digits, hyphen, underscore; no leading or trailing hyphen.</summary>
    [GeneratedRegex(@"^[0-9A-Za-z_](?:[0-9A-Za-z_-]{0,61}[0-9A-Za-z_])?$", RegexOptions.CultureInvariant)]
    private static partial Regex LabelRegex();

    /// <summary>A MAC in either separator style.</summary>
    [GeneratedRegex(@"^(?:[0-9a-fA-F]{2}[:\-]){5}[0-9a-fA-F]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex MacShapedRegex();

    [GeneratedRegex(@"^(?:[0-9a-f]{2}:){5}[0-9a-f]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalMacRegex();

    public const int MaxNameChars = 63;
    public const int MaxNotesChars = 400;

    private readonly ISqlConnectionFactory _factory;
    private readonly ILogger<DeviceTruthStore> _log;

    public DeviceTruthStore(ISqlConnectionFactory factory, ILogger<DeviceTruthStore> log)
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

    /// <summary>The Entra object id, as <see cref="SavedReportStore.OwnerOid"/> resolves it.</summary>
    public static string? EditorOid(ClaimsPrincipal user) => SavedReportStore.OwnerOid(user);

    public static string? EditorName(ClaimsPrincipal user) => SavedReportStore.OwnerName(user);

    /// <summary>
    /// Normalises a MAC to the lower-case colon form the schema stores, or null
    /// when it is not a MAC at all. The dashed form is accepted because that is
    /// how Windows and the DNS console spell it, and pasting from there is the
    /// obvious thing to do.
    /// </summary>
    public static string? NormaliseMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac))
        {
            return null;
        }
        var m = mac.Trim().ToLowerInvariant().Replace('-', ':');
        return CanonicalMacRegex().IsMatch(m) ? m : null;
    }

    /// <summary>
    /// Why a name cannot be stated, or null when it can. Mirrors the CHECK
    /// constraints deliberately: the database is the backstop, but a constraint
    /// violation surfaces as an unreadable SQL error, and whoever is typing
    /// deserves to be told which rule they broke.
    /// </summary>
    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "A name is required.";
        }
        var n = name.Trim();
        if (n.Length > MaxNameChars)
        {
            return $"A DNS label cannot exceed {MaxNameChars} characters.";
        }
        if (n.Contains('.'))
        {
            return "Enter the host name only, without a domain - the zone is added when it is published.";
        }
        if (MacShapedRegex().IsMatch(n))
        {
            return "That is a MAC address, not a name. Naming devices after their MAC is the problem this fixes.";
        }
        if (!LabelRegex().IsMatch(n))
        {
            return "Use letters, digits, hyphen or underscore only, and do not start or end with a hyphen.";
        }
        return null;
    }

    /// <summary>
    /// Every known device. <paramref name="onlyNeedsReview"/> narrows it to the
    /// worklist: no stated name, and nothing better than the device talking
    /// about itself.
    /// </summary>
    public async Task<IReadOnlyList<DeviceRow>> ListAsync(
        bool onlyNeedsReview = false, CancellationToken ct = default)
    {
        const string sql = """
            SELECT mac, device_name, current_ip, name_source, ip_source, ip_observed_utc,
                   vendor, device_type, publish_dns, notes, stated_by, stated_utc,
                   ad_dns_name, ad_dns_ip, ad_dns_agrees, candidate_names, needs_review
            FROM dbo.vDeviceTruth
            WHERE (@onlyReview = 0 OR needs_review = 1)
            ORDER BY needs_review DESC,
                     CASE WHEN device_name IS NULL THEN 1 ELSE 0 END,
                     device_name,
                     mac;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        await using var cmd = Command(conn, sql);
        cmd.Parameters.Add(new SqlParameter("@onlyReview", onlyNeedsReview ? 1 : 0));

        var rows = new List<DeviceRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            rows.Add(new DeviceRow
            {
                Mac = r.GetString(0),
                DeviceName = r.IsDBNull(1) ? null : r.GetString(1),
                CurrentIp = r.IsDBNull(2) ? null : r.GetString(2),
                NameSource = r.GetString(3),
                IpSource = r.IsDBNull(4) ? null : r.GetString(4),
                IpObservedUtc = r.IsDBNull(5) ? null : r.GetDateTime(5),
                Vendor = r.IsDBNull(6) ? null : r.GetString(6),
                DeviceType = r.IsDBNull(7) ? null : r.GetString(7),
                PublishDns = !r.IsDBNull(8) && r.GetBoolean(8),
                Notes = r.IsDBNull(9) ? null : r.GetString(9),
                StatedBy = r.IsDBNull(10) ? null : r.GetString(10),
                StatedUtc = r.IsDBNull(11) ? null : r.GetDateTime(11),
                AdDnsName = r.IsDBNull(12) ? null : r.GetString(12),
                AdDnsIp = r.IsDBNull(13) ? null : r.GetString(13),
                AdDnsAgrees = r.IsDBNull(14) ? null : r.GetBoolean(14),
                CandidateNames = r.IsDBNull(15) ? 0 : r.GetInt32(15),
                NeedsReview = !r.IsDBNull(16) && r.GetBoolean(16),
            });
        }
        return rows;
    }

    /// <summary>
    /// Every name every source offered, keyed by MAC.
    /// <para>
    /// Deliberately unfiltered - it includes the names <c>dbo.vDeviceName</c>
    /// rejects, such as "wlan0" and bare MACs. Seeing that the controller only
    /// ever knew this device as "wlan0" is exactly the context needed to name
    /// it, and hiding that would make the editor look arbitrary.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyDictionary<string, List<DeviceCandidate>>> CandidatesAsync(
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT mac, source, name, ip, observed_utc
            FROM dbo.DeviceNameObservation
            WHERE LEN(LTRIM(RTRIM(name))) > 0
            ORDER BY mac,
                     CASE source
                         WHEN 'omada'     THEN 1
                         WHEN 'mdns'      THEN 2
                         WHEN 'ssdp'      THEN 3
                         WHEN 'snmp'      THEN 4
                         WHEN 'netbios'   THEN 5
                         WHEN 'addns'     THEN 6
                         WHEN 'omadadhcp' THEN 7
                         WHEN 'nmap'      THEN 8
                         ELSE 9
                     END;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        await using var cmd = Command(conn, sql);

        var map = new Dictionary<string, List<DeviceCandidate>>(StringComparer.OrdinalIgnoreCase);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var mac = r.GetString(0);
            if (!map.TryGetValue(mac, out var list))
            {
                list = [];
                map[mac] = list;
            }
            list.Add(new DeviceCandidate(
                r.GetString(1),
                r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.GetDateTime(4)));
        }
        return map;
    }

    /// <summary>
    /// States a name for a MAC, inserting or replacing the row.
    /// <para>
    /// The MAC is normalised and the name validated before this is called, but
    /// both are re-checked here: this is the only write path to the network's
    /// authoritative name table, and it must not depend on a page having been
    /// careful.
    /// </para>
    /// </summary>
    public async Task SetAsync(
        string mac, string name, string? notes, bool publishDns,
        ClaimsPrincipal user, CancellationToken ct = default)
    {
        var canonical = NormaliseMac(mac)
                        ?? throw new ArgumentException($"Not a MAC address: {mac}", nameof(mac));
        var problem = ValidateName(name);
        if (problem is not null)
        {
            throw new ArgumentException(problem, nameof(name));
        }

        var oid = EditorOid(user)
                  ?? throw new InvalidOperationException(
                      "No object id claim on the signed-in principal; cannot attribute the change.");

        if (notes is { Length: > MaxNotesChars })
        {
            notes = notes[..MaxNotesChars];
        }

        // MERGE rather than a delete-then-insert: the row is the authoritative
        // name, and it should never be momentarily absent.
        const string sql = """
            MERGE dbo.DeviceTruth AS target
            USING (SELECT @mac AS mac) AS src ON target.mac = src.mac
            WHEN MATCHED THEN UPDATE SET
                device_name     = @name,
                notes           = @notes,
                publish_dns     = @publish,
                updated_by_oid  = @oid,
                updated_by_name = @who,
                updated_utc     = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT
                (mac, device_name, notes, publish_dns, updated_by_oid, updated_by_name, updated_utc)
                VALUES (@mac, @name, @notes, @publish, @oid, @who, SYSUTCDATETIME());
            """;

        await using var conn = await _factory.OpenWriteAsync(ct);
        await using var cmd = Command(conn, sql);
        cmd.Parameters.Add(new SqlParameter("@mac", canonical));
        cmd.Parameters.Add(new SqlParameter("@name", name.Trim()));
        cmd.Parameters.Add(new SqlParameter("@notes",
            (object?)(string.IsNullOrWhiteSpace(notes) ? null : notes.Trim()) ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@publish", publishDns));
        cmd.Parameters.Add(new SqlParameter("@oid", oid));
        cmd.Parameters.Add(new SqlParameter("@who", (object?)EditorName(user) ?? DBNull.Value));

        await cmd.ExecuteNonQueryAsync(ct);
        _log.LogInformation("Device name stated: {Mac} = {Name} by {Who}",
            canonical, name.Trim(), EditorName(user) ?? oid);
    }

    /// <summary>
    /// Withdraws a stated name, so the device falls back to precedence again.
    /// The observations are untouched - this removes the assertion, not the
    /// evidence.
    /// </summary>
    public async Task ClearAsync(string mac, ClaimsPrincipal user, CancellationToken ct = default)
    {
        var canonical = NormaliseMac(mac)
                        ?? throw new ArgumentException($"Not a MAC address: {mac}", nameof(mac));

        await using var conn = await _factory.OpenWriteAsync(ct);
        await using var cmd = Command(conn, "DELETE FROM dbo.DeviceTruth WHERE mac = @mac;");
        cmd.Parameters.Add(new SqlParameter("@mac", canonical));
        await cmd.ExecuteNonQueryAsync(ct);

        _log.LogInformation("Device name withdrawn: {Mac} by {Who}",
            canonical, EditorName(user) ?? EditorOid(user));
    }
}
