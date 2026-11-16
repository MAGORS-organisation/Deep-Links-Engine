using Dle.Control.Configuration;
using Dle.Control.Identity;

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Dle.Control.Features.Shared;

/// <summary>
/// Requires that the caller administers the instance, not merely a tenant.
/// </summary>
/// <remarks>
/// Tenant management is the one control-plane operation that legitimately crosses tenants, so
/// holding the owner role inside some tenant cannot be enough on its own — every tenant has an
/// owner, and an owner who could provision tenants could provision one whose data they then read.
/// </remarks>
public sealed record DleInstanceOperatorRequirement : IAuthorizationRequirement;

/// <summary>
/// Evaluates <see cref="DleInstanceOperatorRequirement"/> against the deployment's configuration.
/// </summary>
/// <remarks>
/// <para>
/// Multi-tenant instance administration is gated strictly to callers whose tenant matches
/// <c>Dle:Control:InstanceTenantId</c> (or the bootstrap operator tenant registered on
/// <see cref="DleInstanceOperator"/>).
/// </para>
/// <para>
/// When <c>Dle:Control:AllowTenantSelfService</c> is switched on without an explicit
/// <c>InstanceTenantId</c>, the deployment operates in single-tenant self-service mode: only the
/// operator tenant (or the sole active tenant when none was discovered by bootstrap) satisfies the
/// requirement, and provisioning additional tenants is blocked so another tenant can never acquire
/// cross-tenant privileges.
/// </para>
/// </remarks>
public sealed class DleInstanceOperatorHandler : AuthorizationHandler<DleInstanceOperatorRequirement>
{
    private readonly IOptionsMonitor<DleControlOptions> _options;
    private readonly DleInstanceOperator _operator;
    private readonly IServiceScopeFactory? _scopeFactory;

    /// <summary>Creates the handler.</summary>
    /// <param name="options">Control-plane options, read on every evaluation.</param>
    /// <param name="instanceOperator">Knows the operator tenant.</param>
    /// <param name="scopeFactory">Scope factory used to verify single-tenant boundaries.</param>
    public DleInstanceOperatorHandler(
        IOptionsMonitor<DleControlOptions> options,
        DleInstanceOperator instanceOperator,
        IServiceScopeFactory? scopeFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(instanceOperator);
        _options = options;
        _operator = instanceOperator;
        _scopeFactory = scopeFactory;
    }

    /// <inheritdoc />
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        DleInstanceOperatorRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        DleCaller? caller = context.User.GetDleCaller();

        if (caller is null)
        {
            return;
        }

        if (_operator.IsOperator(caller.TenantId))
        {
            context.Succeed(requirement);
            return;
        }

        if (_options.CurrentValue.AllowTenantSelfService
            && _operator.TenantId is null
            && _scopeFactory is not null)
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            TenantRepository tenants = scope.ServiceProvider.GetRequiredService<TenantRepository>();
            IReadOnlyList<Tenant> existing = await tenants.ListAsync(
                includeDeleted: false,
                limit: 2,
                CancellationToken.None);

            if (existing.Count == 0 || (existing.Count == 1 && existing[0].Id == caller.TenantId))
            {
                context.Succeed(requirement);
            }
        }
    }
}
