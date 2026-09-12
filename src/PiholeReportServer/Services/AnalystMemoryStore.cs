using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;
using PiholeReportServer.Data;
using PiholeReportServer.Models;

namespace PiholeReportServer.Services;

/// <summary>
/// Standing facts the Analyst has been told, in <c>dbo.AnalystMemory</c>.
/// <para>
/// The conversation itself is in memory and short-lived, which suits "now just for
/// that device" and not "the Pixel is Jason's phone". The warehouse only knows
/// hostnames, so without somewhere to keep that second kind of fact the question
/// "what has Jason's phone been doing" is unanswerable no matter how good the model
/// is. These persist across conversations and restarts.
/// </para>
/// <para>
/// Scoped by <c>owner_oid</c> from the signed-in principal, exactly like
/// <see cref="SavedReportStore"/>, so one account cannot read or edit another's.
/// </para>
/// </summary>
public sealed class AnalystMemoryStore
{
    /// <summary>
    /// Ceiling per user. Every fact goes into every prompt, so this is a real budget:
    /// at roughly 20 tokens a fact, 60 costs about 1,200 prompt tokens, which the GPU
    /// host evaluates in well under a second but which would crowd a smaller context.
    /// </summary>
    public const int MaxPerUser = 60;

    public const int MaxSubjectChars = 100;
    public const int MaxTargetChars = 255;
    public const int MaxFactChars = 500;

    private readonly ISqlConnectionFactory _factory;
    private readonly ILogger<AnalystMemoryStore> _log;

    public AnalystMemoryStore(ISqlConnectionFactory factory, ILogger<AnalystMemoryStore> log)
    {
        _factory = factory;
        _log = log;
    }

    public async Task<IReadOnlyList<AnalystFact>> ListAsync(
        string ownerOid, CancellationToken ct = default)
    {
        const string sql = """
            SELECT id, kind, subject, target, fact, created_utc, updated_utc, used_count
            FROM dbo.AnalystMemory
            WHERE owner_oid = @owner
            ORDER BY CASE kind WHEN 'device' THEN 0 ELSE 1 END, subject;
            """;

        var list = new List<AnalystFact>();
        try
        {
            await using var conn = await _factory.OpenAsync(ct);
            await using var cmd = Command(conn, sql);
            cmd.Parameters.Add("@owner", SqlDbType.VarChar, 64).Value = ownerOid;

            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                list.Add(new AnalystFact
                {
                    Id = r.GetInt32(0),
                    Kind = r.GetString(1),
                    Subject = r.GetString(2),
                    Target = r.IsDBNull(3) ? null : r.GetString(3),
                    Fact = r.GetString(4),
                    CreatedUtc = r.GetDateTime(5),
                    UpdatedUtc = r.GetDateTime(6),
                    UsedCount = r.GetInt32(7),
                });
            }
        }
        catch (SqlException ex) when (ex.Number is 208 or 4060)
        {
            // Table not deployed yet. Memory is an enhancement, not a prerequisite -
            // the agent must still answer questions, so this is not fatal.
            _log.LogWarning("dbo.AnalystMemory is missing; the Analyst has no memory. Run tools/schema/AnalystMemory.sql.");
        }
        return list;
    }

    /// <summary>
    /// Stores or corrects one fact. Teaching the same subject twice updates it rather
    /// than adding a second, contradictory entry — being told "actually the Pixel is
    /// Sam's" has to replace what was there, not sit alongside it.
    /// </summary>
    public async Task<AnalystMemoryResult> RememberAsync(
        string ownerOid,
        string? ownerName,
        string kind,
        string subject,
        string? target,
        string fact,
        CancellationToken ct = default)
    {
        kind = kind is "device" or "rename" or "preference" ? kind : "note";
        subject = Trim(subject, MaxSubjectChars);
        target = string.IsNullOrWhiteSpace(target) ? null : Trim(target, MaxTargetChars);
        fact = Trim(fact, MaxFactChars);

        if (string.IsNullOrWhiteSpace(subject))
        {
            return AnalystMemoryResult.Rejected("A fact needs a subject — what should I call this?");
        }
        if (string.IsNullOrWhiteSpace(fact))
        {
            return AnalystMemoryResult.Rejected("A fact needs some content.");
        }
        if (kind == "device" && target is null)
        {
            return AnalystMemoryResult.Rejected(
                "A device alias needs the name the network knows it by, so I can turn it into a filter.");
        }
        if (kind == "rename" && target is null)
        {
            return AnalystMemoryResult.Rejected(
                "A rename needs the value to look for as well as what to show instead.");
        }

        const string sql = """
            SET NOCOUNT ON;

            IF (SELECT COUNT(*) FROM dbo.AnalystMemory WHERE owner_oid = @owner) >= @cap
               AND NOT EXISTS (SELECT 1 FROM dbo.AnalystMemory
                               WHERE owner_oid = @owner AND subject = @subject)
            BEGIN
                SELECT -1 AS id;
                RETURN;
            END

            UPDATE dbo.AnalystMemory
               SET kind = @kind, target = @target, fact = @fact,
                   owner_name = @ownerName, updated_utc = SYSUTCDATETIME()
             WHERE owner_oid = @owner AND subject = @subject;

            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO dbo.AnalystMemory (owner_oid, owner_name, kind, subject, target, fact)
                VALUES (@owner, @ownerName, @kind, @subject, @target, @fact);
                SELECT CONVERT(int, SCOPE_IDENTITY()) AS id;
            END
            ELSE
            BEGIN
                SELECT id FROM dbo.AnalystMemory WHERE owner_oid = @owner AND subject = @subject;
            END
            """;

        try
        {
            await using var conn = await _factory.OpenAsync(ct);
            await using var cmd = Command(conn, sql);
            cmd.Parameters.Add("@owner", SqlDbType.VarChar, 64).Value = ownerOid;
            cmd.Parameters.Add("@ownerName", SqlDbType.NVarChar, 200).Value = (object?)ownerName ?? DBNull.Value;
            cmd.Parameters.Add("@kind", SqlDbType.VarChar, 10).Value = kind;
            cmd.Parameters.Add("@subject", SqlDbType.NVarChar, MaxSubjectChars).Value = subject;
            cmd.Parameters.Add("@target", SqlDbType.NVarChar, MaxTargetChars).Value = (object?)target ?? DBNull.Value;
            cmd.Parameters.Add("@fact", SqlDbType.NVarChar, MaxFactChars).Value = fact;
            cmd.Parameters.Add("@cap", SqlDbType.Int).Value = MaxPerUser;

            var id = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? -1);
            if (id < 0)
            {
                return AnalystMemoryResult.Rejected(
                    $"I am already remembering {MaxPerUser} things. Forget one first.");
            }

            _log.LogInformation("Analyst memory: remembered {Kind} '{Subject}' for {Owner}.",
                kind, subject, ownerOid);
            return AnalystMemoryResult.Stored(id, fact);
        }
        catch (SqlException ex) when (ex.Number is 208 or 4060)
        {
            return AnalystMemoryResult.Rejected(
                "I cannot remember anything yet — dbo.AnalystMemory has not been created. " +
                "Run tools/schema/AnalystMemory.sql.");
        }
    }

    /// <summary>Forgets by subject, which is what a person can actually name.</summary>
    public async Task<bool> ForgetAsync(string ownerOid, string subject, CancellationToken ct = default)
    {
        const string sql = """
            DELETE FROM dbo.AnalystMemory
            WHERE owner_oid = @owner AND subject = @subject;
            """;
        try
        {
            await using var conn = await _factory.OpenAsync(ct);
            await using var cmd = Command(conn, sql);
            cmd.Parameters.Add("@owner", SqlDbType.VarChar, 64).Value = ownerOid;
            cmd.Parameters.Add("@subject", SqlDbType.NVarChar, MaxSubjectChars).Value = Trim(subject, MaxSubjectChars);
            return await cmd.ExecuteNonQueryAsync(ct) > 0;
        }
        catch (SqlException ex) when (ex.Number is 208 or 4060)
        {
            return false;
        }
    }

    public async Task<bool> ForgetAsync(string ownerOid, int id, CancellationToken ct = default)
    {
        const string sql = "DELETE FROM dbo.AnalystMemory WHERE owner_oid = @owner AND id = @id;";
        try
        {
            await using var conn = await _factory.OpenAsync(ct);
            await using var cmd = Command(conn, sql);
            cmd.Parameters.Add("@owner", SqlDbType.VarChar, 64).Value = ownerOid;
            cmd.Parameters.Add("@id", SqlDbType.Int).Value = id;
            return await cmd.ExecuteNonQueryAsync(ct) > 0;
        }
        catch (SqlException ex) when (ex.Number is 208 or 4060)
        {
            return false;
        }
    }

    /// <summary>Counts a prompt that carried these facts, so unused ones are visible.</summary>
    public async Task MarkUsedAsync(string ownerOid, CancellationToken ct = default)
    {
        try
        {
            await using var conn = await _factory.OpenAsync(ct);
            await using var cmd = Command(conn,
                "UPDATE dbo.AnalystMemory SET used_count = used_count + 1 WHERE owner_oid = @owner;");
            cmd.Parameters.Add("@owner", SqlDbType.VarChar, 64).Value = ownerOid;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            // A counter is not worth failing a question over.
            _log.LogDebug(ex, "Could not update Analyst memory use counters.");
        }
    }

    /// <summary>
    /// Renders the facts for the system prompt. Device aliases come first and carry
    /// the filter to use, because those are the ones that decide whether a question
    /// can be answered at all rather than merely answered with more context.
    /// <para>
    /// The list of facts is omitted when there are none — an empty "Devices:" heading
    /// costs tokens and invites the model to fill it. The instructions for HOW to
    /// remember are not omitted, and used to be: they lived inside this method's
    /// early return, so a model with nothing remembered was never told it could
    /// remember anything. The first fact could only ever be taught through the panel,
    /// which is the one moment someone is most likely to try telling it instead.
    /// </para>
    /// </summary>
    public static string Render(IReadOnlyList<AnalystFact> facts)
    {
        var sb = new StringBuilder();

        if (facts.Count == 0)
        {
            AppendHowToRemember(sb);
            return sb.ToString();
        }

        sb.AppendLine("WHAT YOU HAVE BEEN TOLD BY THE OWNER");
        sb.AppendLine("Standing facts and instructions. Treat the facts as true and follow the");
        sb.AppendLine("instructions without being reminded of them.");

        var devices = facts.Where(f => f.Kind == "device").ToList();
        if (devices.Count > 0)
        {
            sb.AppendLine("Devices:");
            foreach (var d in devices)
            {
                // Give the model the clause, not just the mapping: it reliably reuses a
                // filter it has been handed and less reliably builds one from prose.
                sb.Append("- \"").Append(d.Subject).Append("\" is the device ")
                  .Append(d.Target)
                  .Append(" — match it with ").Append(FilterFor(d.Target!)).AppendLine();
            }
        }

        // Preferences are instructions, not facts, so they get their own heading and
        // an explicit "follow these without being asked". Mixed in with facts the
        // model treats them as background colour and ignores them.
        var prefs = facts.Where(f => f.Kind == "preference").ToList();
        if (prefs.Count > 0)
        {
            sb.AppendLine("How they want results presented - follow these every time,");
            sb.AppendLine("without being reminded and without mentioning that you did:");
            foreach (var pref in prefs)
            {
                sb.Append("- ").AppendLine(pref.Fact);
            }
        }

        // The model does not perform renames - they are applied to the output after
        // the query runs. It is told about them only so its prose uses the same
        // label the table will show, instead of the raw hostname.
        var renames = facts.Where(f => f.Kind == "rename").ToList();
        if (renames.Count > 0)
        {
            sb.AppendLine("Display names already applied to the table for them:");
            foreach (var r in renames)
            {
                sb.Append("- ").Append(r.Target).Append(" and its variations appear as \"")
                  .Append(r.Subject).AppendLine("\" - use that name when you refer to it.");
            }
            sb.AppendLine("Do NOT try to do this substitution in SQL; query the real values.");
        }

        var notes = facts.Where(f => f.Kind == "note").ToList();
        if (notes.Count > 0)
        {
            sb.AppendLine("Other facts:");
            foreach (var n in notes)
            {
                sb.Append("- ").AppendLine(n.Fact);
            }
        }

        sb.AppendLine();
        AppendHowToRemember(sb);
        return sb.ToString();
    }

    /// <summary>
    /// How to be taught a standing fact, and how to be told to drop one. Always in the
    /// prompt, with or without facts — see <see cref="Render"/>.
    /// </summary>
    private static void AppendHowToRemember(StringBuilder sb)
    {
        // The worked example uses a deliberately fictional device. An example naming a
        // real one reads as a competing fact, and the model then has to choose between
        // the illustration and the truth.
        sb.AppendLine("To be told something new to remember, reply with:");
        sb.AppendLine("  {\"action\":\"remember\",\"kind\":\"device\",\"subject\":\"the spare laptop\",");
        sb.AppendLine("   \"target\":\"EXAMPLE-HOST-1\",\"fact\":\"The spare laptop is EXAMPLE-HOST-1\",");
        sb.AppendLine("   \"answer\":\"Noted - I'll remember that.\"}");
        sb.AppendLine("Kinds:");
        sb.AppendLine("  device      an alias for a machine; target is the hostname, IP or MAC.");
        sb.AppendLine("  preference  how they want results presented, e.g. \"always order");
        sb.AppendLine("              descending\" or \"chart time series\". No target.");
        sb.AppendLine("  rename      show one value in place of another; subject is what to");
        sb.AppendLine("              display, target is the value to replace.");
        sb.AppendLine("  note        any other standing fact. No target.");
        sb.AppendLine("Only remember when they are TELLING you something to keep, not when they");
        sb.AppendLine("ask a question. \"Show me X\" is a question; \"always show me X\" is a");
        sb.AppendLine("preference worth remembering.");
        // Documented alongside remember because the pair is what makes a wrong fact
        // correctable in conversation; without it the only route is the panel.
        sb.AppendLine("To be told to drop one, reply with:");
        sb.AppendLine("  {\"action\":\"forget\",\"subject\":\"the spare laptop\",");
        sb.AppendLine("   \"answer\":\"Forgotten.\"}");
    }

    /// <summary>
    /// A MAC address: exactly six hex pairs. Deliberately strict, because counting
    /// separators does not work — a compressed IPv6 address such as
    /// <c>fd9b:25cc:e1bc:6d52::1</c> has the same five colons as a MAC, and treating
    /// it as one emits <c>dc.mac = '&lt;ipv6&gt;'</c>, which matches nothing while
    /// looking entirely plausible.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex MacPattern =
        new(@"^([0-9a-fA-F]{2}[:-]){5}[0-9a-fA-F]{2}$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// The clause that matches a target, chosen by what it actually is. A MAC is the
    /// device's real identity and survives a DHCP change, an IP is exact, and a
    /// hostname is matched by prefix because reverse DNS suffixes it with the domain
    /// so an equality test on the bare name finds nothing.
    /// </summary>
    internal static string FilterFor(string target)
    {
        var t = target.Trim();

        // MAC first: it is the unambiguous shape, and no MAC parses as an IP address.
        if (MacPattern.IsMatch(t))
        {
            return $"dc.mac = '{t.ToLowerInvariant()}'";
        }
        if (System.Net.IPAddress.TryParse(t, out var ip))
        {
            return $"q.client = '{ip}'";
        }
        return $"dc.name LIKE '{t.Replace("'", "''")}%'";
    }

    private static string Trim(string? s, int max)
    {
        s = (s ?? "").Trim();
        return s.Length <= max ? s : s[..max];
    }

    private static SqlCommand Command(SqlConnection conn, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandType = CommandType.Text;
        return cmd;
    }
}

/// <summary>Outcome of teaching the Analyst a fact.</summary>
public sealed record AnalystMemoryResult(bool Ok, int Id, string? Fact, string? Problem)
{
    public static AnalystMemoryResult Stored(int id, string fact) => new(true, id, fact, null);

    public static AnalystMemoryResult Rejected(string problem) => new(false, 0, null, problem);
}
