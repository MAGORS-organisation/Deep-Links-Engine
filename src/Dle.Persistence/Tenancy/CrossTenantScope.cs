namespace Dle.Persistence.Tenancy;

/// <summary>
/// An open, deliberate window in which a <see cref="DleDbContext"/> may read across tenants.
/// </summary>
/// <remarks>
/// <para>
/// Cross-tenant work exists — the webhook delivery worker, partition maintenance, the
/// administrative tenant list — and pretending otherwise would only push it into
/// <c>IgnoreQueryFilters()</c> calls scattered through the code base. Instead it is funnelled
/// through <see cref="DleDbContext.BeginCrossTenantScope"/>, which is a single greppable call, takes
/// a written reason, and lasts only as long as the <c>using</c> block.
/// </para>
/// <para>
/// Opening the scope does not by itself widen anything: the query still has to drop the tenant
/// filter by name. What the scope changes is that the tenant filter parameter stops throwing when
/// no tenant is in scope, so a cross-tenant query works without an ambient tenant.
/// </para>
/// </remarks>
public sealed class CrossTenantScope : IDisposable
{
    private readonly DleDbContext _context;
    private bool _disposed;

    internal CrossTenantScope(DleDbContext context, string reason)
    {
        _context = context;
        Reason = reason;
    }

    /// <summary>Why the scope was opened. Recorded for diagnostics and for review.</summary>
    public string Reason { get; }

    /// <summary>Closes the scope and restores tenant-scoped behaviour.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _context.EndCrossTenantScope();
    }
}
