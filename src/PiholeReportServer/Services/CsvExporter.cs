using System.Globalization;
using CsvHelper;
using PiholeReportServer.Models;

namespace PiholeReportServer.Services;

public static class CsvExporter
{
    public static async Task WriteAsync(Stream target, QueryResult result, CancellationToken ct = default)
    {
        // leaveOpen so the ASP.NET response stream is not closed underneath us.
        await using var writer = new StreamWriter(target, leaveOpen: true);
        await using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture, leaveOpen: true);

        foreach (var c in result.Columns)
        {
            csv.WriteField(c.Name);
        }
        await csv.NextRecordAsync();

        foreach (var row in result.Rows)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var cell in row)
            {
                csv.WriteField(Format(cell));
            }
            await csv.NextRecordAsync();
        }

        await csv.FlushAsync();
        await writer.FlushAsync(ct);
    }

    private static string Format(object? v) => v switch
    {
        null => "",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    /// <summary>Builds a safe, descriptive download name.</summary>
    public static string FileName(string stem)
    {
        var clean = new string(stem
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')
            .ToArray())
            .Trim('-');

        if (clean.Length == 0)
        {
            clean = "report";
        }

        return $"{clean}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.csv";
    }
}
