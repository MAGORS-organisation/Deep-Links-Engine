using Microsoft.AspNetCore.Authorization;

namespace Dle.Control.Identity;

/// <summary>
/// Requires a minimum role and, where the endpoint names one, a scope (FR-242).
/// </summary>
/// <param name="MinimumRole">Weakest role that satisfies the policy.</param>
/// <remarks>
/// Expressed as a minimum rather than as a set of accepted role names so that the ordering in
/// <see cref="DleRoles"/> is the single definition of who outranks whom. A credential whose role
/// this build does not recognise ranks below every requirement and therefore satisfies none.
/// </remarks>
public sealed record DleRoleRequirement(string MinimumRole) : IAuthorizationRequirement;

/// <summary>
/// Requires that the caller's scope list permits an operation.
/// </summary>
/// <param name="Scope">The scope the endpoint needs, from <see cref="DleScopes"/>.</param>
public sealed record DleScopeRequirement(string Scope) : IAuthorizationRequirement;

/// <summary>
/// Evaluates <see cref="DleRoleRequirement"/> against the caller.
/// </summary>
public sealed class DleRoleAuthorizationHandler : AuthorizationHandler<DleRoleRequirement>
{
    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        DleRoleRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        DleCaller? caller = context.User.GetDleCaller();

        // No caller, no tenant, or an unknown role: the requirement is simply not met. Nothing here
        // calls Fail(), because failing would also veto any other policy evaluated in the same
        // request; not succeeding is enough and is what leaves the outcome a deny.
        if (caller is not null
            && DleRoles.Rank(caller.Role) >= DleRoles.Rank(requirement.MinimumRole)
            && DleRoles.IsKnown(caller.Role))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Evaluates <see cref="DleScopeRequirement"/> against the caller.
/// </summary>
public sealed class DleScopeAuthorizationHandler : AuthorizationHandler<DleScopeRequirement>
{
    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        DleScopeRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        DleCaller? caller = context.User.GetDleCaller();

        if (caller is not null && caller.HasScope(requirement.Scope))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
