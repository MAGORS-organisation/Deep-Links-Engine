using Dle.Domain.Entities;

namespace Dle.Persistence.Repositories;

/// <summary>
/// Read-only lookup for resolving a tenant's existence and status during credential or token validation.
/// </summary>
public interface ITenantLookup
{
    /// <summary>Finds a tenant by its unique identifier.</summary>
    /// <param name="id">The tenant identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tenant, or <see langword="null"/> when none exists.</returns>
    Task<Tenant?> FindByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Finds a tenant by its unique slug.</summary>
    /// <param name="slug">The tenant slug.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The tenant, or <see langword="null"/> when none exists.</returns>
    Task<Tenant?> FindBySlugAsync(string slug, CancellationToken cancellationToken);
}
