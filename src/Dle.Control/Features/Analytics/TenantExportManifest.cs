namespace Dle.Control.Features.Analytics;

/// <summary>
/// The manifest at the root of a tenant data export (FR-249).
/// </summary>
/// <remarks>
/// An export without a manifest is a pile of files. The manifest is what makes it an artefact
/// somebody can act on two years later: it names the schema version the rows were written under,
/// what each file contains, how many rows it has, and — the part auditors ask about — exactly what
/// was deliberately left out and why.
/// </remarks>
public sealed record TenantExportManifest
{
    /// <summary>Version of the export layout. Incremented when a file's shape changes.</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>Tenant the export belongs to.</summary>
    public required Guid TenantId { get; init; }

    /// <summary>Tenant slug, so the archive is identifiable without opening the rows.</summary>
    public required string TenantSlug { get; init; }

    /// <summary>When the export was produced, in UTC.</summary>
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Name of the product and the version that produced it.</summary>
    public required string Producer { get; init; }

    /// <summary>Row count per file, keyed by file name.</summary>
    public required IReadOnlyDictionary<string, long> Counts { get; init; }

    /// <summary>
    /// What the export deliberately omits, and why. Credential material is never exported: a copy
    /// of every API key hash and webhook secret in a file that leaves the building is a worse
    /// outcome than an incomplete export.
    /// </summary>
    public required IReadOnlyList<string> Omissions { get; init; }
}
