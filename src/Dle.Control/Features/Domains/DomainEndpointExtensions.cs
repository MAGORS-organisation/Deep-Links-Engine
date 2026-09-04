using Dle.Control.Configuration;
using Dle.Control.Features.Domains;
using Dle.Control.Features.Shared;
using Dle.Control.Identity;

using Microsoft.AspNetCore.RateLimiting;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Maps the domain management routes (SHARED-KERNEL §15, FR-143, FR-145).
/// </summary>
public static class DomainEndpointExtensions
{
    /// <summary>Maps <c>/api/v1/domains</c> and everything beneath it.</summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    public static IEndpointRouteBuilder MapDomains(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder domains = app.MapGroup("/api/v1/domains").WithTags("Domains");

        domains.MapGet("/", ManageDomains.ListAsync)
            .RequireAuthorization(DlePolicies.DomainsRead)
            .RequireRateLimiting(DleRateLimitPolicies.Read)
            .WithName("ListDomains")
            .WithSummary("Lists the hosts this tenant serves links from.")
            .Produces<PagedResponse<DomainResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        domains.MapGet("/{id:guid}", ManageDomains.GetAsync)
            .RequireAuthorization(DlePolicies.DomainsRead)
            .RequireRateLimiting(DleRateLimitPolicies.Read)
            .WithName("GetDomain")
            .WithSummary("Reads one domain and its verification state.")
            .Produces<DomainResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        domains.MapPost("/", ManageDomains.CreateAsync)
            .RequireAuthorization(DlePolicies.DomainsWrite)
            .RequireRateLimiting(DleRateLimitPolicies.Write)
            .WithIdempotency()
            .WithName("CreateDomain")
            .WithSummary("Registers a host.")
            .WithDescription(
                "Each subdomain needs its own association file and its own entitlement; nothing is "
                + "inherited from the parent domain. A consent mode override may only tighten the "
                + "tenant setting, never widen it.")
            .Produces<DomainResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        domains.MapPatch("/{id:guid}", ManageDomains.UpdateAsync)
            .RequireAuthorization(DlePolicies.DomainsWrite)
            .RequireRateLimiting(DleRateLimitPolicies.Write)
            .WithIdempotency()
            .WithName("UpdateDomain")
            .WithSummary("Changes a domain.")
            .WithDescription(
                "The host itself cannot be changed: every short URL in circulation names it. Moving "
                + "to a new host is register-and-retire.")
            .Produces<DomainResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        domains.MapDelete("/{id:guid}", ManageDomains.DeleteAsync)
            .RequireAuthorization(DlePolicies.DomainsWrite)
            .RequireRateLimiting(DleRateLimitPolicies.Write)
            .WithIdempotency()
            .WithName("DeleteDomain")
            .WithSummary("Removes a host that serves no links.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        domains.MapPost("/{id:guid}/verify", VerifyDomain.HandleAsync)
            .RequireAuthorization(DlePolicies.DomainsWrite)
            .RequireRateLimiting(DleRateLimitPolicies.Write)
            .WithName("VerifyDomain")
            .WithSummary("Fetches and checks the association files of a host.")
            .WithDescription(
                "Checks name resolution, the certificate, the Apple App Site Association file and the "
                + "Digital Asset Links file. A redirect on either association file is a failure, "
                + "because both platforms refuse a redirected file. The response always carries the "
                + "seven-day propagation notice: a device that already holds the old file keeps it "
                + "until its cached copy expires.")
            .Produces<DomainVerificationResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        domains.MapGet("/{id:guid}/verifications", VerifyDomain.HistoryAsync)
            .RequireAuthorization(DlePolicies.DomainsRead)
            .RequireRateLimiting(DleRateLimitPolicies.Read)
            .WithName("ListDomainVerifications")
            .WithSummary("Reads the verification history of a host.")
            .WithDescription(
                "Association file problems are usually intermittent, so the useful question is since "
                + "when rather than right now.")
            .Produces<PagedResponse<DomainVerificationRecord>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }
}
