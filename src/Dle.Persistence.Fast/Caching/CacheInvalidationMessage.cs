namespace Dle.Persistence.Fast.Caching;

/// <summary>
/// Cache invalidation message published across edge replicas via Valkey pub/sub.
/// </summary>
public sealed record CacheInvalidationMessage(
    string Type,
    string? Host = null,
    string? Slug = null,
    Guid? TenantId = null);
