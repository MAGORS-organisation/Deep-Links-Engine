namespace Dle.Control.Infrastructure;

/// <summary>
/// The stored lifecycle values of <c>tenants.status</c> (§B.5.2).
/// </summary>
/// <remarks>
/// Stored as text rather than as an integer so the value is legible in the database and survives a
/// reordering of any enumeration (SHARED-KERNEL §12). <see cref="Deleted"/> is what the soft-delete
/// query filter keys off, which is why it is never written by an update: deletion goes through the
/// repository that also writes the audit entry.
/// </remarks>
public static class TenantStatuses
{
    /// <summary>The tenant serves links and its credentials authenticate.</summary>
    public const string Active = "active";

    /// <summary>
    /// The tenant is switched off. Its credentials stop authenticating; its links keep resolving,
    /// because withdrawing a short URL is an abuse decision, not a billing one.
    /// </summary>
    public const string Suspended = "suspended";

    /// <summary>The tenant is deleted. Hidden by the soft-delete filter, kept for the audit trail.</summary>
    public const string Deleted = "deleted";

    /// <summary>Statuses an update may set.</summary>
    public static readonly string[] Settable = [Active, Suspended];

    /// <summary>Whether a value is one an update may set.</summary>
    /// <param name="status">The candidate value.</param>
    /// <returns><see langword="true"/> for <see cref="Active"/> and <see cref="Suspended"/>.</returns>
    public static bool IsSettable(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        foreach (string candidate in Settable)
        {
            if (string.Equals(candidate, status, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
