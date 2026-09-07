using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PiholeReportServer.Configuration;
using PiholeReportServer.Data;
using PiholeReportServer.Models;

namespace PiholeReportServer.Services;

public sealed record NightlyProbe(string Name, string Question);

public sealed class NightlyFinding
{
    public required int Id { get; init; }
    public required DateTime RunUtc { get; init; }
    public required DateOnly CoversDate { get; init; }
    public required string Probe { get; init; }
    public required string Question { get; init; }
    public required string Finding { get; init; }
    public required string Severity { get; init; }
    public string? SqlUsed { get; init; }
    public int? RowCount { get; init; }
    public DateTime? DismissedUtc { get; init; }
}

/// <summary>
/// Runs the standing battery of questions once a night and stores what it found, so
/// anomalies surface without anyone asking.
/// <para>
/// Deliberately sequential and slow. Each probe is a full agent run — several
/// generations plus database queries — and the inference host is CPU-only and shared
/// with SQL Server. Running probes in parallel would make the database contend with
/// itself at exactly the moment nothing is watching.
/// </para>
/// </summary>
public sealed class NightlyAnalysisService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly AiOptions _opt;
    private readonly ILogger<NightlyAnalysisService> _log;

    public NightlyAnalysisService(
        IServiceScopeFactory scopes,
        IOptions<AiOptions> opt,
        ILogger<NightlyAnalysisService> log)
    {
        _scopes = scopes;
        _opt = opt.Value;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opt.Enabled || !_opt.NightlyAnalysisEnabled)
        {
            _log.LogInformation("Nightly analysis is disabled by configuration.");
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            var delay = UntilNextRun(DateTime.Now, _opt.NightlyAnalysisHour);
            _log.LogInformation("Nightly analysis: next run in {Hours:F1}h.", delay.TotalHours);

            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return;   // shutting down
            }

            try
            {
                await RunBatteryAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failed night must not kill the service; tomorrow should still run.
                _log.LogError(ex, "Nightly analysis failed.");
            }
        }
    }

    internal static TimeSpan UntilNextRun(DateTime now, int hour)
    {
        var target = now.Date.AddHours(Math.Clamp(hour, 0, 23));
        if (target <= now)
        {
            target = target.AddDays(1);
        }
        return target - now;
    }

    private async Task RunBatteryAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var agent = scope.ServiceProvider.GetRequiredService<AiAgent>();
        var store = scope.ServiceProvider.GetRequiredService<NightlyFindingStore>();

        var probes = await store.GetEnabledProbesAsync(ct);
        if (probes.Count == 0)
        {
            _log.LogInformation("Nightly analysis: no probes enabled.");
            return;
        }

        var runUtc = DateTime.UtcNow;
        var covers = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        _log.LogInformation("Nightly analysis: {Count} probe(s) for {Date}.", probes.Count, covers);

        foreach (var probe in probes)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var run = await agent.RunAsync(probe.Question, ct);

                // Store even an inconclusive run: "the agent could not determine this"
                // is more useful than a silent gap when reviewing a week of nights.
                var finding = run.HasAnswer
                    ? run.Answer!
                    : $"No conclusion reached ({run.Outcome}). {run.Error}".Trim();

                await store.SaveAsync(
                    runUtc, covers, probe.Name, probe.Question, finding,
                    Severity(finding, run), FirstSql(run), TotalRows(run),
                    (int)run.Elapsed.TotalMilliseconds, ct);

                _log.LogInformation(
                    "Nightly probe {Probe}: {Outcome} in {Sec:F0}s, {Steps} step(s).",
                    probe.Name, run.Outcome, run.Elapsed.TotalSeconds, run.Steps.Count);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Nightly probe {Probe} failed.", probe.Name);
            }
        }
    }

    /// <summary>
    /// Severity is inferred from the wording, not asked of the model — a separate
    /// question per probe would double the inference cost of every night, and the
    /// value is only used for ordering and highlighting.
    /// </summary>
    private static string Severity(string finding, AgentRun run)
    {
        if (!run.HasAnswer)
        {
            return "low";
        }

        var text = finding.ToLowerInvariant();
        string[] high =
        [
            "malware", "phishing", "cryptojack", "ransomware", "compromise",
            "exfiltrat", "suspicious", "unusual spike", "never seen before",
        ];
        if (high.Any(text.Contains))
        {
            return "high";
        }

        string[] low = ["no change", "nothing unusual", "no new", "consistent with", "as expected"];
        return low.Any(text.Contains) ? "low" : "normal";
    }

    private static string? FirstSql(AgentRun run) =>
        run.Steps.FirstOrDefault(s => s.Kind == AgentStepKind.Query)?.Sql;

    private static int TotalRows(AgentRun run) =>
        run.Steps.Where(s => s.Kind == AgentStepKind.Query).Sum(s => s.RowCount);
}

/// <summary>Reads the probe list and persists findings.</summary>
public sealed class NightlyFindingStore
{
    private readonly ISqlConnectionFactory _factory;

    public NightlyFindingStore(ISqlConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<NightlyProbe>> GetEnabledProbesAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT probe, question FROM dbo.NightlyProbe
            WHERE enabled = 1 ORDER BY ordinal, probe;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        var list = new List<NightlyProbe>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new NightlyProbe(r.GetString(0), r.GetString(1)));
        }
        return list;
    }

    public async Task SaveAsync(
        DateTime runUtc, DateOnly covers, string probe, string question, string finding,
        string severity, string? sqlUsed, int? rowCount, int? elapsedMs,
        CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO dbo.NightlyFinding
                (run_utc, covers_date, probe, question, finding, severity, sql_used, row_count, elapsed_ms)
            VALUES (@run, @covers, @probe, @question, @finding, @severity, @sql, @rows, @ms);
            """;

        await using var conn = await _factory.OpenWriteAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new SqlParameter("@run", runUtc));
        cmd.Parameters.Add(new SqlParameter("@covers", covers.ToDateTime(TimeOnly.MinValue)));
        cmd.Parameters.Add(new SqlParameter("@probe", probe));
        cmd.Parameters.Add(new SqlParameter("@question", Clip(question, 400)));
        cmd.Parameters.Add(new SqlParameter("@finding", Clip(finding, 2000)));
        cmd.Parameters.Add(new SqlParameter("@severity", severity));
        cmd.Parameters.Add(new SqlParameter("@sql", (object?)Clip(sqlUsed, 4000) ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@rows", (object?)rowCount ?? DBNull.Value));
        cmd.Parameters.Add(new SqlParameter("@ms", (object?)elapsedMs ?? DBNull.Value));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Recent findings for the overview, worst first within each day.</summary>
    public async Task<IReadOnlyList<NightlyFinding>> GetRecentAsync(
        int days = 7, bool includeDismissed = false, CancellationToken ct = default)
    {
        const string sql = """
            SELECT id, run_utc, covers_date, probe, question, finding, severity,
                   sql_used, row_count, dismissed_utc
            FROM dbo.NightlyFinding
            WHERE covers_date >= DATEADD(day, -@days, CAST(SYSUTCDATETIME() AS date))
              AND (@includeDismissed = 1 OR dismissed_utc IS NULL)
            ORDER BY covers_date DESC,
                     CASE severity WHEN 'high' THEN 0 WHEN 'normal' THEN 1 ELSE 2 END,
                     probe;
            """;

        await using var conn = await _factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new SqlParameter("@days", days));
        cmd.Parameters.Add(new SqlParameter("@includeDismissed", includeDismissed ? 1 : 0));

        var list = new List<NightlyFinding>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new NightlyFinding
            {
                Id = r.GetInt32(0),
                RunUtc = r.GetDateTime(1),
                CoversDate = DateOnly.FromDateTime(r.GetDateTime(2)),
                Probe = r.GetString(3),
                Question = r.GetString(4),
                Finding = r.GetString(5),
                Severity = r.GetString(6),
                SqlUsed = r.IsDBNull(7) ? null : r.GetString(7),
                RowCount = r.IsDBNull(8) ? null : r.GetInt32(8),
                DismissedUtc = r.IsDBNull(9) ? null : r.GetDateTime(9),
            });
        }
        return list;
    }

    public async Task DismissAsync(int id, string? who, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE dbo.NightlyFinding
            SET dismissed_utc = SYSUTCDATETIME(), dismissed_by = @who
            WHERE id = @id AND dismissed_utc IS NULL;
            """;

        await using var conn = await _factory.OpenWriteAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new SqlParameter("@id", id));
        cmd.Parameters.Add(new SqlParameter("@who", (object?)Clip(who, 256) ?? DBNull.Value));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string? Clip(string? s, int n) =>
        s is null ? null : (s.Length <= n ? s : s[..n]);
}
