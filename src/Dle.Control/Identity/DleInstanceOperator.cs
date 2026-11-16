using Dle.Control.Configuration;

using Microsoft.Extensions.Options;

namespace Dle.Control.Identity;

/// <summary>
/// Knows which tenant operates the instance — the one whose owners may create and suspend tenants.
/// </summary>
/// <remarks>
/// <para>
/// Configuration wins: <c>Dle:Control:InstanceTenantId</c>, when set, names the operator and nothing
/// else can change it. When it is not set, the bootstrap service reports the tenant it provisioned —
/// or found already provisioned under <c>Dle:Bootstrap:DefaultTenantSlug</c> — on every start, so the
/// operator survives a restart without anyone having to copy an identifier into configuration.
/// </para>
/// <para>
/// This replaces writing into <see cref="IOptionsMonitor{TOptions}.CurrentValue"/>, which lasted only
/// until the next configuration reload or process restart: after one, the first owner silently lost
/// the right to create tenants.
/// </para>
/// </remarks>
public sealed class DleInstanceOperator
{
    private readonly IOptionsMonitor<DleControlOptions> _options;
    private readonly Lock _gate = new();
    private Guid? _discovered;

    /// <summary>Creates the registry.</summary>
    /// <param name="options">Control-plane options; <c>InstanceTenantId</c> is read on every call.</param>
    public DleInstanceOperator(IOptionsMonitor<DleControlOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>The operator tenant, or <see langword="null"/> when the instance has none yet.</summary>
    public Guid? TenantId
    {
        get
        {
            if (_options.CurrentValue.InstanceTenantId is Guid configured && configured != Guid.Empty)
            {
                return configured;
            }

            lock (_gate)
            {
                return _discovered;
            }
        }
    }

    /// <summary>Whether the operator comes from configuration rather than from the bootstrap.</summary>
    public bool IsConfigured =>
        _options.CurrentValue.InstanceTenantId is Guid configured && configured != Guid.Empty;

    /// <summary>Records the tenant the bootstrap provisioned or found.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <exception cref="ArgumentException"><paramref name="tenantId"/> is empty.</exception>
    public void Discovered(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("The operator tenant cannot be the empty identifier.", nameof(tenantId));
        }

        lock (_gate)
        {
            _discovered = tenantId;
        }
    }

    /// <summary>Whether <paramref name="tenantId"/> is the operator tenant.</summary>
    /// <param name="tenantId">The caller's tenant.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public bool IsOperator(Guid tenantId) => TenantId is Guid operatorTenant && operatorTenant == tenantId;
}
