using System.Text.Json.Serialization;

namespace PiholeReportServer.Models;

public enum ParameterKind
{
    Date,
    DateTime,
    Int,
    Text,
    Choice,
}

public sealed class ReportParameter
{
    /// <summary>Name without the '@' prefix, matching the placeholder in the .sql file.</summary>
    public string Name { get; set; } = "";

    public string Label { get; set; } = "";

    public ParameterKind Kind { get; set; } = ParameterKind.Text;

    /// <summary>
    /// Default value. Dates accept a relative form: "today", "-7d", "-24h",
    /// resolved when the report is opened.
    /// </summary>
    public string? Default { get; set; }

    public bool Required { get; set; } = true;

    /// <summary>Allowed values when <see cref="Kind"/> is Choice.</summary>
    public List<string> Choices { get; set; } = [];

    public string? Help { get; set; }
}

public sealed class ReportDefinition
{
    /// <summary>URL-safe identifier; also the .sql file name without extension.</summary>
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    public string Summary { get; set; } = "";

    /// <summary>Grouping shown on the reports index, e.g. "Traffic", "Blocking".</summary>
    public string Category { get; set; } = "General";

    /// <summary>Ordering within a category.</summary>
    public int Order { get; set; }

    public List<ReportParameter> Parameters { get; set; } = [];

    /// <summary>Loaded from the sibling .sql file; not part of the JSON manifest.</summary>
    [JsonIgnore]
    public string Sql { get; set; } = "";
}

public sealed class ReportManifest
{
    public List<ReportDefinition> Reports { get; set; } = [];
}
