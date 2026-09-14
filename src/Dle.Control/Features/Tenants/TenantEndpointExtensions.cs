using Dle.Control.Configuration;
using Dle.Control.Features.Shared;
using Dle.Control.Features.Tenants;
using Dle.Control.Identity;

using Microsoft.AspNetCore.RateLimiting;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Maps the tenant management routes (SHARED-KERNEL §15, FR-241).
/// </summary>
/// <remarks>
/// Everything except <c>/tenants/me</c> is instance administration, which is why those routes carry
/// the tenant write policy: holding the owner role inside some tenant is not enough to manage
/// another one.
/// </remarks>
public static class TenantEndpointExtensions
{
    /// <summary>Maps <c>/api/v1/tenants</c> and everything beneath it.</summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    public static IEndpointRouteBuilder MapTenants(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder tenants = app.MapGroup("/api/v1/tenants").WithTags("Tenants");

        tenants.MapGet("/me", ManageTenants.GetOwnAsync)
            .RequireAuthorization(DlePolicies.TenantsRead)
            .RequireRateLimiting(DleRateLimitPolicies.Read)
            .WithName("GetOwnTenant")
            .WithSummary("Reads the tenant the presented credential belongs to.")
            .Produces<TenantResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        tenants.MapGet("/", ManageTenants.ListAsync)
            .RequireAuthorization(DlePolicies.TenantsWrite)
            .RequireRateLimiting(DleRateLimitPolicies.Read)
            .WithName("ListTenants")
            .WithSummary("Lists every tenant on the instance.")
            .Produces<PagedResponse<TenantResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        tenants.MapGet("/{id:guid}", ManageTenants.GetAsync)
            .RequireAuthorization(DlePolicies.TenantsWrite)
            .RequireRateLimiting(DleRateLimitPolicies.Read)
            .WithName("GetTenant")
            .WithSummary("Reads one tenant.")
            .Produces<TenantResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        tenants.MapPost("/", ManageTenants.CreateAsync)
            .RequireAuthorization(DlePolicies.TenantsWrite)
            .RequireRateLimiting(DleRateLimitPolicies.Write)
            .WithIdempotency()
            .WithName("CreateTenant")
            .WithSummary("Provisions a tenant.")
            .WithDescription(
                "The consent mode defaults to aggregate_only, the mode that needs no consent banner, "
                + "so a fresh tenant never collects more than the legitimate interest basis supports "
                + "until somebody deliberately widens it.")
            .Produces<TenantResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        tenants.MapPatch("/{id:guid}", ManageTenants.UpdateAsync)
            .RequireAuthorization(DlePolicies.TenantsWrite)
            .RequireRateLimiting(DleRateLimitPolicies.Write)
            .WithIdempotency()
            .WithName("UpdateTenant")
            .WithSummary("Changes a tenant's name, consent mode or status.")
            .WithDescription(
                "The slug is immutable: it appears in the audit trail, and a trail whose subject can "
                + "be renamed underneath it stops being evidence. A consent mode change is recorded "
                + "with both the old and the new value and drops every cached snapshot of the tenant.")
            .Produces<TenantResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        tenants.MapDelete("/{id:guid}", ManageTenants.DeleteAsync)
            .RequireAuthorization(DlePolicies.TenantsWrite)
            .RequireRateLimiting(DleRateLimitPolicies.Write)
            .WithIdempotency()
            .WithName("DeleteTenant")
            .WithSummary("Deactivates a tenant without removing its rows.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }
}
