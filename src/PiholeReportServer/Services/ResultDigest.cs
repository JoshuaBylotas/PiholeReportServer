using System.Globalization;
using System.Text;
using PiholeReportServer.Models;

namespace PiholeReportServer.Services;

/// <summary>
/// Condenses a result set into a compact, exact statistical summary for the model to
/// narrate.
/// <para>
/// Sending raw rows was measured at ~150 seconds for 100 rows on the CPU-only
/// inference host, and produced a row-by-row recital rather than an insight: a code
/// model given a table of numbers spends its tokens restating them. Computing the
/// statistics here is instant and exact, leaves the model the job it is actually good
/// at — writing prose — and cuts the prompt from ~2,100 tokens to ~250.
/// </para>
/// </summary>
public static class ResultDigest
{
    /// <summary>Distinct values listed per text column.</summary>
    private const int TopValuesPerColumn = 5;

    /// <summary>Columns described. Wide results are truncated rather than blowing up the prompt.</summary>
    private const int MaxColumns = 8;

    private static readonly HashSet<string> NumericTypes =
    [
        "Int32", "Int64", "Int16", "Byte", "Decimal", "Double", "Single"
    ];

    public static string Build(QueryResult result, string context)
    {
        var sb = new StringBuilder();
        sb.Append("CONTEXT: ").AppendLine(context);
        sb.Append("ROWS: ").Append(result.Rows.Count.ToString("N0", CultureInfo.InvariantCulture));
        if (result.Truncated)
        {
            sb.Append(" (truncated at the row cap, so totals below are partial)");
        }
        sb.AppendLine();

        var columnCount = Math.Min(result.Columns.Count, MaxColumns);
        if (result.Columns.Count > MaxColumns)
        {
            sb.Append("NOTE: describing the first ").Append(MaxColumns)
              .Append(" of ").Append(result.Columns.Count).AppendLine(" columns.");
        }

        for (var c = 0; c < columnCount; c++)
        {
            var col = result.Columns[c];
            sb.Append("- ").Append(col.Name).Append(": ");

            if (NumericTypes.Contains(col.ClrType))
            {
                DescribeNumeric(sb, result, c);
            }
            else
            {
                DescribeCategorical(sb, result, c);
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void DescribeNumeric(StringBuilder sb, QueryResult result, int c)
    {
        double sum = 0, min = double.MaxValue, max = double.MinValue;
        var n = 0;

        foreach (var row in result.Rows)
        {
            if (row[c] is null)
            {
                continue;
            }
            try
            {
                var v = Convert.ToDouble(row[c], CultureInfo.InvariantCulture);
                sum += v;
                if (v < min) { min = v; }
                if (v > max) { max = v; }
                n++;
            }
            catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
            {
                // A column typed numeric that holds something unconvertible should not
                // abort the whole digest.
            }
        }

        if (n == 0)
        {
            sb.Append("no numeric values");
            return;
        }

        sb.Append("total ").Append(Fmt(sum))
          .Append(", average ").Append(Fmt(sum / n))
          .Append(", range ").Append(Fmt(min)).Append('–').Append(Fmt(max))
          .Append(" across ").Append(n.ToString("N0", CultureInfo.InvariantCulture)).Append(" values");
    }

    private static void DescribeCategorical(StringBuilder sb, QueryResult result, int c)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var nulls = 0;

        foreach (var row in result.Rows)
        {
            if (row[c] is null)
            {
                nulls++;
                continue;
            }
            var key = row[c] is DateTime dt
                ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : row[c]!.ToString() ?? "";
            counts[key] = counts.TryGetValue(key, out var v) ? v + 1 : 1;
        }

        if (counts.Count == 0)
        {
            sb.Append("all null");
            return;
        }

        sb.Append(counts.Count.ToString("N0", CultureInfo.InvariantCulture)).Append(" distinct");
        if (nulls > 0)
        {
            sb.Append(", ").Append(nulls.ToString("N0", CultureInfo.InvariantCulture)).Append(" null");
        }

        var top = counts.OrderByDescending(kv => kv.Value)
                        .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                        .Take(TopValuesPerColumn)
                        .ToList();

        // Only worth listing when there is actually concentration to report.
        if (counts.Count > 1)
        {
            sb.Append("; most frequent: ");
            sb.AppendJoin(", ", top.Select(kv =>
                $"{Clip(kv.Key)} ({kv.Value.ToString("N0", CultureInfo.InvariantCulture)})"));
        }
    }

    private static string Fmt(double v) =>
        Math.Abs(v - Math.Round(v)) < 0.0001
            ? ((long)Math.Round(v)).ToString("N0", CultureInfo.InvariantCulture)
            : v.ToString("N2", CultureInfo.InvariantCulture);

    private static string Clip(string s) =>
        s.Length <= 48 ? s : s[..45] + "...";
}
