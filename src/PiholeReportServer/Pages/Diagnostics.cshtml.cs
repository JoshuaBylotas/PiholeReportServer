using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using PiholeReportServer.Configuration;
using PiholeReportServer.Data;
using PiholeReportServer.Services;

namespace PiholeReportServer.Pages;

public sealed class DiagnosticsModel : PageModel
{
    private readonly ISqlConnectionFactory _factory;
    private readonly SqlOptions _sql;
    private readonly AiClient _ai;
    private readonly AiOptions _aiOptions;

    public DiagnosticsModel(
        ISqlConnectionFactory factory,
        IOptions<SqlOptions> sql,
        AiClient ai,
        IOptions<AiOptions> aiOptions)
    {
        _factory = factory;
        _sql = sql.Value;
        _ai = ai;
        _aiOptions = aiOptions.Value;
    }

    public string Server => _sql.Server;
    public string Database => _sql.Database;
    public SqlAuthMode AuthMode => _sql.AuthMode;

    public string? SqlIdentity { get; private set; }
    public string? SqlError { get; private set; }

    /// <summary>Table name to row count, or null where the object is missing.</summary>
    public Dictionary<string, long?> Objects { get; } = new();

    /// <summary>
    /// Health of every configured inference host. Never null after a GET: when
    /// inference is switched off the record says so, which is the answer to "is it
    /// up" rather than an empty panel that looks like a failed probe.
    /// </summary>
    public AiHealth Ai { get; private set; } = new();

    /// <summary>
    /// The AI-backed features and whether each is switched on. An inference host can
    /// be perfectly healthy while the thing that would use it is disabled by
    /// configuration, and that pairing is invisible from the host table alone.
    /// </summary>
    public IReadOnlyList<(string Name, bool On, string Detail)> AiFeatures { get; private set; } = [];

    public IEnumerable<Claim> InterestingClaims => User.Claims.Where(c =>
        c.Type is "name" or "preferred_username" or "oid" or "tid" or "roles"
            or ClaimTypes.Name or ClaimTypes.NameIdentifier or ClaimTypes.Role);

    private static readonly string[] Expected =
    [
        "dbo.PiholeQueries",
        "dbo.DimClient",
        "dbo.DimType",
        "dbo.DimStatus",
        "dbo.Adlists",
        "dbo.GravityDomains",
    ];

    public async Task OnGetAsync(CancellationToken ct)
    {
        // Started before the warehouse work and awaited after it. The two are
        // independent, and a SQL server that has gone away must not also cost the AI
        // panel its answer - saying which of them is down is the whole point here.
        var probe = _ai.ProbeAsync(ct);

        try
        {
            await InventoryAsync(ct);
        }
        finally
        {
            Ai = await probe;
            AiFeatures = DescribeAiFeatures();
        }
    }

    private async Task InventoryAsync(CancellationToken ct)
    {
        try
        {
            SqlIdentity = await _factory.DescribeIdentityAsync(ct);
        }
        catch (Exception ex)
        {
            SqlError = ex.Message;
            return;
        }

        try
        {
            await using var conn = await _factory.OpenAsync(ct);

            foreach (var name in Expected)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandTimeout = _sql.CommandTimeoutSeconds;

                // Row counts come from sys.dm_db_partition_stats rather than a
                // COUNT(*), so the page stays fast against a 23M-row table.
                cmd.CommandText = """
                    SELECT SUM(ps.row_count)
                    FROM sys.dm_db_partition_stats AS ps
                    WHERE ps.object_id = OBJECT_ID(@name)
                      AND ps.index_id IN (0, 1);
                    """;
                cmd.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@name", name));

                var value = await cmd.ExecuteScalarAsync(ct);
                Objects[name] = value is null or DBNull ? null : Convert.ToInt64(value);
            }
        }
        catch (Exception ex)
        {
            SqlError = ex.Message;
        }
    }

    /// <summary>
    /// What each AI-backed feature would do if asked right now. A healthy host and a
    /// switched-off feature look identical from the Analyst page - it simply says the
    /// assistant is not configured - so the two flags are shown side by side.
    /// </summary>
    private IReadOnlyList<(string Name, bool On, string Detail)> DescribeAiFeatures()
    {
        var on = Ai.Enabled;
        var live = on && Ai.AnyUp;
        var shared = live
            ? "Ready."
            : on
                ? "Switched on, but no inference host is answering."
                : Ai.Disabled ?? "Off.";

        var nightly = _aiOptions.NightlyAnalysisEnabled;
        var nextRun = DateTime.Now.Add(
            NightlyAnalysisService.UntilNextRun(DateTime.Now, _aiOptions.NightlyAnalysisHour));

        return
        [
            ("Analyst", live, shared),
            ("Ask (natural language to SQL)", live, shared),
            ("Explain (result summaries)", live, shared),
            ("Nightly analysis", on && nightly && live,
                !nightly
                    ? "Off while Ai:NightlyAnalysisEnabled is false. The flag is separate from " +
                      "Ai:Enabled so the interactive features can run without the unattended job."
                    : !on
                        ? $"Switched on, but inference is off, so the job exited at startup. {Ai.Disabled}"
                        : $"Next run {nextRun:ddd d MMM HH:mm} local. Changing the flag needs a restart - " +
                          "the job decides once, at startup."),
        ];
    }
}
