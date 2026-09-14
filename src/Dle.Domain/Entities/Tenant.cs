namespace Dle.Domain.Entities;

/// <summary>
/// A tenant: the unit of ownership and of data isolation (FR-241). Maps to <c>tenants</c>.
/// </summary>
/// <remarks>
/// The entities in this namespace are plain objects. They carry no mapping attributes and no
/// navigation properties; column names, keys, indexes and relationships are configured with the
/// fluent API in the persistence project, so the shared kernel stays free of any dependency on an
/// object relational mapper.
/// </remarks>
public class Tenant
{
    /// <summary>Primary key, a UUIDv7.</summary>
    public Guid Id { get; set; }

    /// <summary>Case insensitive unique short name of the tenant.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Lifecycle state, stored as text: <c>active</c>, <c>suspended</c> or <c>deleted</c>.</summary>
    public string Status { get; set; } = "active";

    /// <summary>Consent mode, stored as text: <c>full</c>, <c>aggregate_only</c> or <c>off</c>.
    /// The default is the middle one, so a fresh tenant never collects more than the legitimate
    /// interest basis supports (§E.6.2, FR-248).</summary>
    public string ConsentMode { get; set; } = "aggregate_only";

    /// <summary>Free form tenant settings, stored as a JSON document.</summary>
    public string Settings { get; set; } = "{}";

    /// <summary>Creation instant, in UTC.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
