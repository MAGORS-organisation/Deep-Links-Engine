namespace Dle.Persistence;

/// <summary>
/// Names of the query filters attached to the model, for use with <c>IgnoreQueryFilters</c>.
/// </summary>
/// <remarks>
/// <para>
/// EF Core 10 allows several named filters per entity type. Before that there was one anonymous
/// slot, so a second filter overwrote the first and the two concerns fought over it: adding a soft
/// delete filter silently removed the tenant filter, which is precisely the leak T-09 describes.
/// With names the two coexist and — just as importantly — can be dropped one at a time, so
/// "show me the quarantined links as well" no longer means "and every other tenant's links too".
/// </para>
/// <para>
/// Never call the parameterless <c>IgnoreQueryFilters()</c>: it drops <see cref="Tenant"/> along
/// with everything else. Use <see cref="DleQueryableExtensions"/>, whose members drop exactly one
/// named filter.
/// </para>
/// </remarks>
public static class DleQueryFilters
{
    /// <summary>
    /// Restricts every tenant-owned entity type to the tenant supplied by
    /// <see cref="Tenancy.ITenantContext"/> (FR-241).
    /// </summary>
    public const string Tenant = "Tenant";

    /// <summary>
    /// Hides rows that have been withdrawn but not deleted: a quarantined link, a revoked API key,
    /// a deleted tenant.
    /// </summary>
    public const string SoftDelete = "SoftDelete";
}
