using System.Text.Json;
using Microsoft.Extensions.Options;
using PiholeReportServer.Configuration;
using PiholeReportServer.Models;

namespace PiholeReportServer.Services;

/// <summary>
/// Loads the pre-canned report definitions from <c>Reports/reports.json</c> and
/// the matching <c>Reports/&lt;id&gt;.sql</c> files. Definitions are cached and
/// reloaded when the manifest changes on disk, so a report can be added or
/// edited on a running server without a redeploy.
/// </summary>
public sealed class ReportCatalog
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly string _root;
    private readonly ILogger<ReportCatalog> _log;
    private readonly Lock _gate = new();

    private IReadOnlyList<ReportDefinition> _cache = [];
    private DateTime _cacheStamp = DateTime.MinValue;

    public ReportCatalog(
        IOptions<ReportingOptions> opt,
        IHostEnvironment env,
        ILogger<ReportCatalog> log)
    {
        var configured = opt.Value.ReportsPath;
        _root = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(env.ContentRootPath, configured);
        _log = log;
    }

    public IReadOnlyList<ReportDefinition> All()
    {
        var manifestPath = Path.Combine(_root, "reports.json");
        if (!File.Exists(manifestPath))
        {
            _log.LogWarning("Report manifest not found at {Path}; no pre-canned reports available.", manifestPath);
            return [];
        }

        var stamp = File.GetLastWriteTimeUtc(manifestPath);

        lock (_gate)
        {
            if (stamp == _cacheStamp && _cache.Count > 0)
            {
                return _cache;
            }

            var json = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<ReportManifest>(json, JsonOpts)
                           ?? new ReportManifest();

            var loaded = new List<ReportDefinition>();
            foreach (var def in manifest.Reports)
            {
                var sqlPath = Path.Combine(_root, $"{def.Id}.sql");
                if (!File.Exists(sqlPath))
                {
                    _log.LogError("Report '{Id}' is declared in reports.json but {Path} is missing; skipping.",
                        def.Id, sqlPath);
                    continue;
                }

                def.Sql = File.ReadAllText(sqlPath);
                loaded.Add(def);
            }

            _cache = loaded
                .OrderBy(r => r.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Order)
                .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _cacheStamp = stamp;

            _log.LogInformation("Loaded {Count} pre-canned report(s) from {Root}.", _cache.Count, _root);
            return _cache;
        }
    }

    public ReportDefinition? Find(string id) =>
        All().FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<IGrouping<string, ReportDefinition>> ByCategory() =>
        All().GroupBy(r => r.Category);

    /// <summary>
    /// Resolves a parameter default, supporting the relative forms "today",
    /// "now", "-7d" and "-24h" so a report can open on a sensible window.
    /// </summary>
    public static string? ResolveDefault(ReportParameter p, DateTime nowUtc)
    {
        var d = p.Default;
        if (string.IsNullOrWhiteSpace(d))
        {
            return null;
        }

        var fmt = p.Kind == ParameterKind.Date ? "yyyy-MM-dd" : "yyyy-MM-ddTHH:mm";

        if (d.Equals("today", StringComparison.OrdinalIgnoreCase))
        {
            return nowUtc.Date.ToString(fmt);
        }
        if (d.Equals("now", StringComparison.OrdinalIgnoreCase))
        {
            return nowUtc.ToString(fmt);
        }

        // -7d / -24h / +1d
        if (d.Length > 2 && (d[0] is '-' or '+'))
        {
            var unit = char.ToLowerInvariant(d[^1]);
            if ((unit is 'd' or 'h') && int.TryParse(d[..^1], out var n))
            {
                var shifted = unit == 'd' ? nowUtc.AddDays(n) : nowUtc.AddHours(n);
                return (p.Kind == ParameterKind.Date ? shifted.Date : shifted).ToString(fmt);
            }
        }

        return d;
    }
}
