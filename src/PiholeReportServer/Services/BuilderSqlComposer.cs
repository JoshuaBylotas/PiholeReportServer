using System.Text;
using PiholeReportServer.Models;
using static PiholeReportServer.Models.BuilderSpec;

namespace PiholeReportServer.Services;

public sealed record ComposedQuery(string Sql, Dictionary<string, object?> Parameters);

/// <summary>
/// Turns a <see cref="BuilderSpec"/> into SQL.
/// <para>
/// Every fragment that lands in the statement is chosen from a fixed map keyed
/// by an enum, so no caller-supplied string is ever concatenated into the SQL.
/// Free-text filters (domain, client) travel as bound parameters. That is what
/// makes the builder safe to expose to any signed-in viewer, unlike the raw-SQL
/// console.
/// </para>
/// </summary>
public static class BuilderSqlComposer
{
    /// <summary>Upper bound on entries in the generated client IN list.</summary>
    public const int MaxSelectedClients = 500;

    private sealed record Dimension(string Expression, string Alias);

    private static readonly Dictionary<GroupDimension, Dimension> Dimensions = new()
    {
        [GroupDimension.Domain]         = new("q.domain", "domain"),
        [GroupDimension.Client]         = new("q.client", "client"),
        [GroupDimension.ClientHostname] = new("COALESCE(dc.hostname, q.client)", "client_name"),
        [GroupDimension.QueryType]      = new("COALESCE(dt.type_text, CONCAT('type ', q.type))", "query_type"),
        [GroupDimension.Status]         = new("COALESCE(ds.status_text, q.status_text)", "status"),
        [GroupDimension.Upstream]       = new("COALESCE(q.forward, '(cache or blocked)')", "upstream"),
        [GroupDimension.Hour]           = new("DATEADD(hour, DATEDIFF(hour, 0, q.ts), 0)", "hour_bucket"),
        [GroupDimension.Day]            = new("CAST(q.ts AS date)", "day"),
        [GroupDimension.Week]           = new("DATEADD(week, DATEDIFF(week, 0, q.ts), 0)", "week_start"),
    };

    private static readonly Dictionary<MetricKind, Dimension> Metrics = new()
    {
        [MetricKind.QueryCount]       = new("COUNT_BIG(*)", "queries"),
        [MetricKind.DistinctDomains]  = new("COUNT(DISTINCT q.domain)", "distinct_domains"),
        [MetricKind.DistinctClients]  = new("COUNT(DISTINCT q.client)", "distinct_clients"),
        [MetricKind.AvgReplyMs]       = new("CAST(AVG(NULLIF(q.reply_time, 0)) * 1000 AS decimal(10,2))", "avg_reply_ms"),
        [MetricKind.MaxReplyMs]       = new("CAST(MAX(q.reply_time) * 1000 AS decimal(10,2))", "max_reply_ms"),
    };

    public static ComposedQuery Compose(BuilderSpec spec, int hardRowCap)
    {
        var p = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        var groups = new List<Dimension>();
        if (Dimensions.TryGetValue(spec.GroupBy, out var g1))
        {
            groups.Add(g1);
        }
        if (spec.ThenBy != spec.GroupBy && Dimensions.TryGetValue(spec.ThenBy, out var g2))
        {
            groups.Add(g2);
        }

        var metric = Metrics.TryGetValue(spec.Metric, out var m) ? m : Metrics[MetricKind.QueryCount];

        var limit = Math.Clamp(spec.Limit, 1, hardRowCap);
        p["limit"] = limit;

        var sb = new StringBuilder();

        // Only join what the chosen dimensions and filters actually need, so
        // the common "top domains" shape stays a single-table scan.
        var needsClientDim = groups.Any(d => d.Alias == "client_name");
        var needsTypeDim = groups.Any(d => d.Alias == "query_type");
        var needsStatusDim = groups.Any(d => d.Alias == "status");

        sb.AppendLine("SELECT TOP (@limit)");
        foreach (var d in groups)
        {
            sb.AppendLine($"       {d.Expression} AS [{d.Alias}],");
        }
        sb.AppendLine($"       {metric.Expression} AS [{metric.Alias}]");
        sb.AppendLine("FROM dbo.PiholeQueries AS q");

        if (needsClientDim)
        {
            sb.AppendLine("     LEFT JOIN dbo.DimClient AS dc ON dc.ip = q.client");
        }
        if (needsTypeDim)
        {
            sb.AppendLine("     LEFT JOIN dbo.DimType AS dt ON dt.type = q.type");
        }
        if (needsStatusDim)
        {
            sb.AppendLine("     LEFT JOIN dbo.DimStatus AS ds ON ds.status = q.status");
        }

        var where = new List<string>();

        if (spec.From.HasValue)
        {
            where.Add("q.ts >= @from");
            p["from"] = spec.From.Value;
        }
        if (spec.To.HasValue)
        {
            where.Add("q.ts < @to");
            p["to"] = spec.To.Value;
        }
        if (!string.IsNullOrWhiteSpace(spec.ClientFilter))
        {
            where.Add("q.client LIKE @clientFilter");
            p["clientFilter"] = $"%{Escape(spec.ClientFilter)}%";
        }

        // Multi-select client picker. Only the parameter *names* are generated
        // (from an index), never the values, so the selection cannot inject SQL
        // however the form is tampered with. Capped so a hand-crafted post
        // cannot build a pathological IN list.
        var chosenClients = spec.ClientIps
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxSelectedClients)
            .ToList();

        if (chosenClients.Count > 0)
        {
            var placeholders = new List<string>(chosenClients.Count);
            for (var i = 0; i < chosenClients.Count; i++)
            {
                var name = $"cli{i}";
                placeholders.Add($"@{name}");
                p[name] = chosenClients[i];
            }
            where.Add($"q.client IN ({string.Join(", ", placeholders)})");
        }
        if (!string.IsNullOrWhiteSpace(spec.DomainFilter))
        {
            where.Add("q.domain LIKE @domainFilter");
            p["domainFilter"] = $"%{Escape(spec.DomainFilter)}%";
        }
        if (spec.StatusFilter.HasValue)
        {
            where.Add("q.status = @status");
            p["status"] = spec.StatusFilter.Value;
        }
        if (spec.TypeFilter.HasValue)
        {
            where.Add("q.type = @type");
            p["type"] = spec.TypeFilter.Value;
        }
        if (spec.OnlyBlocklisted)
        {
            where.Add("EXISTS (SELECT 1 FROM dbo.GravityDomains AS gd WHERE gd.domain = q.domain)");
        }

        if (where.Count > 0)
        {
            sb.AppendLine("WHERE " + string.Join(Environment.NewLine + "  AND ", where));
        }

        if (groups.Count > 0)
        {
            sb.AppendLine("GROUP BY " + string.Join(", ", groups.Select(d => d.Expression)));
        }

        var dir = spec.Sort == SortDirection.Asc ? "ASC" : "DESC";
        sb.AppendLine(groups.Count > 0
            ? $"ORDER BY [{metric.Alias}] {dir}"
            : $"ORDER BY q.ts {dir}");

        sb.Append("OPTION (RECOMPILE);");

        return new ComposedQuery(sb.ToString(), p);
    }

    /// <summary>
    /// Escapes LIKE wildcards so a filter of "192.0.2.5%" matches that literal
    /// text rather than acting as a pattern.
    /// </summary>
    private static string Escape(string s) =>
        s.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
}
