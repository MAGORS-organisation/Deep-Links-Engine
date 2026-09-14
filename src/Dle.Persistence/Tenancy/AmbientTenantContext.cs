namespace Dle.Persistence.Tenancy;

/// <summary>
/// The default <see cref="ITenantContext"/>: the tenant flows with the asynchronous control flow.
/// </summary>
/// <remarks>
/// <para>
/// An <see cref="AsyncLocal{T}"/> is used rather than a scoped service so that the same
/// implementation works in a request pipeline, in a hosted worker and in a test, and so that a
/// tenant established before an <c>await</c> is still in scope after it. The instance is therefore
/// registered as a singleton; the state it holds is per asynchronous flow, not per instance.
/// </para>
/// <para>
/// Scopes nest and restore, which matters for a worker that iterates tenants: entering tenant B
/// inside the scope of tenant A and leaving it again puts A back, instead of clearing the tenant
/// and turning the next query into an exception.
/// </para>
/// </remarks>
public sealed class AmbientTenantContext : ITenantContext
{
    private static readonly AsyncLocal<Guid?> Current = new();

    /// <inheritdoc />
    public Guid? TenantId => Current.Value;

    /// <inheritdoc />
    public Guid RequiredTenantId => Current.Value ?? throw new TenantContextMissingException();

    /// <inheritdoc />
    public IDisposable BeginScope(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("The tenant identifier must not be empty.", nameof(tenantId));
        }

        Guid? previous = Current.Value;
        Current.Value = tenantId;
        return new Scope(previous);
    }

    /// <summary>Restores the tenant that was in scope before the matching <see cref="BeginScope"/>.</summary>
    private sealed class Scope : IDisposable
    {
        private readonly Guid? _previous;
        private bool _disposed;

        internal Scope(Guid? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Current.Value = _previous;
        }
    }
}
