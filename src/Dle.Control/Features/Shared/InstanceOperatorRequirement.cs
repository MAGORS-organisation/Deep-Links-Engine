using Dle.Control.Configuration;
using Dle.Control.Identity;

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Dle.Control.Features.Shared;

/// <summary>
/// Requires that the caller administers the instance, not merely a tenant (FR-241).
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
/// There are exactly two ways to satisfy it. Either <c>Dle:Control:InstanceTenantId</c> names the
/// tenant whose owners run the instance — the multi-tenant answer — or
/// <c>Dle:Control:AllowTenantSelfService</c> is switched on, which only makes sense for a deployment
/// serving one organisation.
/// </para>
/// <para>
/// With neither set, the requirement is not met and the tenant routes deny every caller. That is the
/// correct posture for a deployment that has not decided: an unconfigured instance should refuse to
/// let anybody create tenants rather than let everybody (SHARED-KERNEL §17.9).
/// </para>
/// </remarks>
public sealed class DleInstanceOperatorHandler : AuthorizationHandler<DleInstanceOperatorRequirement>
{
    private readonly IOptionsMonitor<DleControlOptions> _options;

    /// <summary>Creates the handler.</summary>
    /// <param name="options">Control-plane options, read on every evaluation.</param>
    public DleInstanceOperatorHandler(IOptionsMonitor<DleControlOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        DleInstanceOperatorRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        DleCaller? caller = context.User.GetDleCaller();

        if (caller is null)
        {
            return Task.CompletedTask;
        }

        DleControlOptions options = _options.CurrentValue;

        bool isOperator = options.AllowTenantSelfService
            || (options.InstanceTenantId is Guid instance && instance == caller.TenantId);

        if (isOperator)
        {
            context.Succeed(requirement);
        }

        // Nothing calls Fail(): not succeeding is enough, and failing would veto every other policy
        // evaluated in the same request.
        return Task.CompletedTask;
    }
}
