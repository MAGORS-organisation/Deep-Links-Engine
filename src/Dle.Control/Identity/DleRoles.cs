namespace Dle.Control.Identity;

/// <summary>
/// The four control-plane roles (FR-242).
/// </summary>
/// <remarks>
/// The roles are totally ordered: an owner can do everything an admin can, an admin everything an
/// editor can, an editor everything a viewer can. <see cref="Rank"/> is that order, and every policy
/// is expressed as a minimum rank rather than as a set of role names, so adding a role later cannot
/// silently widen an existing policy.
/// </remarks>
public static class DleRoles
{
    /// <summary>Full control, including issuing and revoking credentials and deleting the tenant.</summary>
    public const string Owner = "owner";

    /// <summary>Manages configuration: domains, applications, webhooks and keys.</summary>
    public const string Admin = "admin";

    /// <summary>Creates and edits links; may not change tenant configuration or credentials.</summary>
    public const string Editor = "editor";

    /// <summary>Reads links, configuration and reports; writes nothing.</summary>
    public const string Viewer = "viewer";

    /// <summary>Every role, weakest first.</summary>
    public static readonly string[] All = [Viewer, Editor, Admin, Owner];

    /// <summary>
    /// Position of a role in the ordering, or <c>-1</c> for anything unrecognised.
    /// </summary>
    /// <param name="role">The stored role value.</param>
    /// <returns>0 for viewer through 3 for owner; <c>-1</c> when the value is not a known role.</returns>
    /// <remarks>
    /// An unknown role ranks below every policy rather than above any, so a credential carrying a
    /// role this build does not understand can do nothing at all (SHARED-KERNEL §17.9).
    /// </remarks>
    public static int Rank(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return -1;
        }

        for (int i = 0; i < All.Length; i++)
        {
            if (string.Equals(All[i], role, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Whether a role is one this build knows.</summary>
    /// <param name="role">The candidate value.</param>
    /// <returns><see langword="true"/> when the role is recognised.</returns>
    public static bool IsKnown(string? role) => Rank(role) >= 0;
}
