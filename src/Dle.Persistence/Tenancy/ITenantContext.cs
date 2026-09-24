namespace Dle.Persistence.Tenancy;

/// <summary>
/// The tenant every control-plane query and write is scoped to (FR-241, T-09).
/// </summary>
/// <remarks>
/// <para>
/// This is the single source of the tenant identifier for <see cref="DleDbContext"/>. The context
/// turns it into a named query filter that is attached to every tenant-owned entity type, so
/// isolation is a property of the model rather than of the discipline of whoever writes the next
/// query. Authentication middleware establishes the tenant once per request with
/// <see cref="BeginScope"/>; background workers do the same per unit of work.
/// </para>
/// <para>
/// There is deliberately no setter and no "current tenant or all tenants" mode. Work that has to
/// cross tenants goes through <see cref="DleDbContext.BeginCrossTenantScope"/>, which is explicit,
/// greppable and requires a written reason.
/// </para>
/// </remarks>
public interface ITenantContext
{
    /// <summary>
    /// The tenant currently in scope, or <see langword="null"/> when none has been established.
    /// </summary>
    Guid? TenantId { get; }

    /// <summary>
    /// The tenant currently in scope.
    /// </summary>
    /// <exception cref="TenantContextMissingException">No tenant has been established.</exception>
    /// <remarks>
    /// The query filters read this property, so a query issued outside a tenant scope fails loudly
    /// instead of quietly returning rows of every tenant.
    /// </remarks>
    Guid RequiredTenantId { get; }

    /// <summary>
    /// Establishes <paramref name="tenantId"/> as the tenant in scope until the returned handle is
    /// disposed. Scopes nest; disposal restores the previous tenant.
    /// </summary>
    /// <param name="tenantId">The tenant to enter. Must not be <see cref="Guid.Empty"/>.</param>
    /// <returns>A handle that restores the previous tenant when disposed.</returns>
    /// <exception cref="ArgumentException"><paramref name="tenantId"/> is <see cref="Guid.Empty"/>.</exception>
    IDisposable BeginScope(Guid tenantId);
}
