namespace Dle.Persistence;

/// <summary>
/// The only sanctioned ways to drop a query filter.
/// </summary>
/// <remarks>
/// Both members name the single filter they remove, so the tenant filter survives unless the caller
/// asks for exactly that — and asking for exactly that is a one-line, greppable, reviewable event.
/// The parameterless <c>IgnoreQueryFilters()</c> is never used anywhere in this project.
/// </remarks>
public static class DleQueryableExtensions
{
    /// <summary>
    /// Includes rows hidden by the soft delete filter: quarantined links, revoked API keys, deleted
    /// tenants. The tenant filter stays on.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="source">The query.</param>
    /// <returns>The query with the soft delete filter removed.</returns>
    public static IQueryable<TEntity> IncludeSoftDeleted<TEntity>(this IQueryable<TEntity> source)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.IgnoreQueryFilters([DleQueryFilters.SoftDelete]);
    }

    /// <summary>
    /// Removes the tenant filter. Only legitimate inside a
    /// <see cref="DleDbContext.BeginCrossTenantScope"/>, which states why the query spans tenants.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="source">The query.</param>
    /// <returns>The query with the tenant filter removed.</returns>
    public static IQueryable<TEntity> AcrossTenants<TEntity>(this IQueryable<TEntity> source)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.IgnoreQueryFilters([DleQueryFilters.Tenant]);
    }
}
