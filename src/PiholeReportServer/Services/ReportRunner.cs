using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PiholeReportServer.Configuration;
using PiholeReportServer.Data;
using PiholeReportServer.Models;

namespace PiholeReportServer.Services;

/// <summary>
/// Executes report SQL and materialises a row-capped result.
/// <para>
/// The row cap is applied while reading the result set rather than by rewriting
/// the caller's SQL. Rewriting is unreliable — a common table expression cannot
/// simply be wrapped in a derived table, and appending TOP would collide with
/// an existing one — whereas stopping the reader is exact and works for every
/// shape of query.
/// </para>
/// </summary>
public sealed class ReportRunner
{
    private readonly ISqlConnectionFactory _factory;
    private readonly SqlOptions _sql;
    private readonly ReportingOptions _reporting;
    private readonly ILogger<ReportRunner> _log;

    public ReportRunner(
        ISqlConnectionFactory factory,
        IOptions<SqlOptions> sql,
        IOptions<ReportingOptions> reporting,
        ILogger<ReportRunner> log)
    {
        _factory = factory;
        _sql = sql.Value;
        _reporting = reporting.Value;
        _log = log;
    }

    public Task<QueryResult> RunAsync(
        string sql,
        IReadOnlyDictionary<string, object?> parameters,
        int? rowCap = null,
        CancellationToken ct = default)
        => ExecuteAsync(sql, parameters, rowCap ?? _reporting.MaxRows, ct);

    /// <summary>Runs a pre-canned report, binding only the parameters it declares.</summary>
    public async Task<QueryResult> RunReportAsync(
        ReportDefinition report,
        IReadOnlyDictionary<string, string?> rawValues,
        int? rowCap = null,
        CancellationToken ct = default)
    {
        var bound = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in report.Parameters)
        {
            rawValues.TryGetValue(p.Name, out var raw);

            if (string.IsNullOrWhiteSpace(raw))
            {
                if (p.Required)
                {
                    throw new ArgumentException($"Parameter '{p.Label}' is required.");
                }
                bound[p.Name] = null;
                continue;
            }

            bound[p.Name] = Coerce(p, raw);
        }

        return await ExecuteAsync(report.Sql, bound, rowCap ?? _reporting.MaxRows, ct);
    }

    private static object? Coerce(ReportParameter p, string raw) => p.Kind switch
    {
        ParameterKind.Date or ParameterKind.DateTime =>
            DateTime.TryParse(raw, out var dt)
                ? dt
                : throw new ArgumentException($"'{p.Label}' is not a valid date/time."),

        ParameterKind.Int =>
            int.TryParse(raw, out var i)
                ? i
                : throw new ArgumentException($"'{p.Label}' must be a whole number."),

        ParameterKind.Choice =>
            p.Choices.Contains(raw, StringComparer.OrdinalIgnoreCase)
                ? raw
                : throw new ArgumentException($"'{raw}' is not an allowed value for '{p.Label}'."),

        _ => raw,
    };

    private async Task<QueryResult> ExecuteAsync(
        string sql,
        IReadOnlyDictionary<string, object?> parameters,
        int rowCap,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        await using var conn = await _factory.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandType = CommandType.Text;
        cmd.CommandTimeout = _sql.CommandTimeoutSeconds;

        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.Add(new SqlParameter($"@{name.TrimStart('@')}", value ?? DBNull.Value));
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var columns = new List<QueryColumn>(reader.FieldCount);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            columns.Add(new QueryColumn(
                reader.GetName(i) is { Length: > 0 } n ? n : $"col{i + 1}",
                reader.GetFieldType(i)?.Name ?? "Object"));
        }

        var rows = new List<object?[]>();
        var truncated = false;

        while (await reader.ReadAsync(ct))
        {
            if (rows.Count >= rowCap)
            {
                truncated = true;
                break;
            }

            var row = new object?[reader.FieldCount];
            reader.GetValues(row);
            for (var i = 0; i < row.Length; i++)
            {
                if (row[i] is DBNull)
                {
                    row[i] = null;
                }
            }
            rows.Add(row);
        }

        sw.Stop();

        if (truncated)
        {
            _log.LogInformation("Result truncated at the {Cap}-row cap after {Ms}ms.", rowCap, sw.ElapsedMilliseconds);
        }

        return new QueryResult
        {
            Columns = columns,
            Rows = rows,
            Truncated = truncated,
            RowCap = rowCap,
            Elapsed = sw.Elapsed,
        };
    }
}
