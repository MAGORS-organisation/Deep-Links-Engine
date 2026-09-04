namespace Dle.Control.Features.Tenants;

/// <summary>
/// Body of <c>PATCH /api/v1/tenants/{id}</c>. Every member is optional; only the supplied ones
/// change.
/// </summary>
/// <remarks>
/// The slug is immutable. It appears in operator-facing URLs and in the audit trail, and an audit
/// trail whose subject can be renamed underneath it stops being evidence (FR-246).
/// </remarks>
public sealed record UpdateTenantRequest
{
    /// <summary>New display name.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// New consent mode: <c>off</c>, <c>aggregate_only</c> or <c>full</c> (FR-248, §E.6.2).
    /// </summary>
    /// <remarks>
    /// Tightening takes effect on the next resolve; widening does not retroactively make already
    /// collected data lawful, which is why the change is recorded in the audit trail with the old
    /// and the new value.
    /// </remarks>
    public string? ConsentMode { get; init; }

    /// <summary>New lifecycle status: <c>active</c> or <c>suspended</c>.</summary>
    /// <remarks>
    /// <c>deleted</c> is not accepted here. Deletion goes through <c>DELETE</c>, which is the
    /// operation the audit trail and the soft-delete filter are both written around.
    /// </remarks>
    public string? Status { get; init; }
}
