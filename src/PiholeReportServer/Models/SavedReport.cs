using System.ComponentModel.DataAnnotations;

namespace PiholeReportServer.Models;

public enum SavedReportKind
{
    /// <summary>Payload is a JSON-serialised <see cref="BuilderSpec"/>.</summary>
    Builder,

    /// <summary>Payload is raw SQL text, re-validated by SqlGuard on every run.</summary>
    Sql,
}

/// <summary>
/// A report a user saved for themselves. Ownership is keyed on the Entra ID
/// <c>oid</c> claim, which is immutable — a UPN changes when someone is renamed and
/// would orphan their saved reports.
/// </summary>
public sealed class SavedReport
{
    public int Id { get; init; }

    public required string OwnerOid { get; init; }

    public string? OwnerName { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public SavedReportKind Kind { get; init; }

    /// <summary>BuilderSpec JSON, or SQL text, depending on <see cref="Kind"/>.</summary>
    public required string Payload { get; init; }

    public DateTime CreatedUtc { get; init; }

    public DateTime UpdatedUtc { get; init; }

    public int RunCount { get; init; }

    public DateTime? LastRunUtc { get; init; }
}

/// <summary>Form input for saving a report from either the builder or the console.</summary>
public sealed class SaveReportInput
{
    [Required(ErrorMessage = "Give the report a name.")]
    [StringLength(128, MinimumLength = 1)]
    [Display(Name = "Name")]
    public string? Name { get; set; }

    [StringLength(512)]
    [Display(Name = "Description")]
    public string? Description { get; set; }
}
