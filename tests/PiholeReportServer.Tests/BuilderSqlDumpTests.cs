using PiholeReportServer.Models;
using PiholeReportServer.Services;
using Xunit;
using static PiholeReportServer.Models.BuilderSpec;

namespace PiholeReportServer.Tests;

/// <summary>
/// Writes every shape of builder-generated SQL to disk so
/// <c>tools/Validate-ReportSql.ps1</c> can bind it against a real database.
/// <para>
/// The unit tests can prove the composer emits *something* the guard accepts, but
/// only SQL Server can prove the column names and joins are real — and a wrong
/// column in a dimension expression would otherwise reach production unnoticed,
/// which is exactly how <c>DimClient.hostname</c> shipped.
/// </para>
/// </summary>
public class BuilderSqlDumpTests
{
    private static string DumpDir =>
        Path.Combine(Path.GetTempPath(), "builder-sql");

    [Fact]
    public void Dump_every_builder_shape_for_schema_validation()
    {
        if (Directory.Exists(DumpDir))
        {
            Directory.Delete(DumpDir, recursive: true);
        }
        Directory.CreateDirectory(DumpDir);

        var written = 0;

        // Every group-by x then-by x metric combination.
        foreach (var group in Enum.GetValues<GroupDimension>())
        {
            foreach (var then in Enum.GetValues<GroupDimension>())
            {
                foreach (var metric in Enum.GetValues<MetricKind>())
                {
                    var spec = new BuilderSpec
                    {
                        GroupBy = group,
                        ThenBy = then,
                        Metric = metric,
                        From = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
                        To = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc),
                    };
                    Write($"{group}-{then}-{metric}", spec);
                    written++;
                }
            }
        }

        // Every filter, applied together so all joins and predicates appear at once.
        Write("all-filters", new BuilderSpec
        {
            GroupBy = GroupDimension.ClientHostname,
            ThenBy = GroupDimension.QueryType,
            Metric = MetricKind.AvgReplyMs,
            From = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            To = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc),
            ClientFilter = "192.0.2.",
            DomainFilter = "example",
            StatusFilter = 2,
            TypeFilter = 1,
            OnlyBlocklisted = true,
            ClientIps = ["192.0.2.10", "192.0.2.11"],
            Sort = SortDirection.Asc,
        });
        written++;

        // No grouping at all — the ORDER BY q.ts branch.
        Write("ungrouped", new BuilderSpec
        {
            GroupBy = GroupDimension.None,
            ThenBy = GroupDimension.None,
            Metric = MetricKind.QueryCount,
        });
        written++;

        Assert.True(written > 100, $"expected a broad sweep, wrote only {written}");
        Assert.True(Directory.EnumerateFiles(DumpDir, "*.sql").Any());
    }

    private static void Write(string name, BuilderSpec spec)
    {
        var composed = BuilderSqlComposer.Compose(spec, 5000);

        // Anything the composer emits must also satisfy the read-only guard.
        var verdict = SqlGuard.Validate(composed.Sql);
        Assert.True(verdict.Allowed, $"{name}: {verdict.Reason}\n{composed.Sql}");

        var safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        File.WriteAllText(Path.Combine(DumpDir, $"{safe}.sql"), composed.Sql);
    }
}
