using Dle.Control.Configuration;
using Dle.Control.Features.Apps;
using Dle.Control.Features.Shared;
using Dle.Control.Identity;

using Microsoft.AspNetCore.RateLimiting;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Maps the application registration routes (SHARED-KERNEL §15, FR-141, FR-142, FR-144).
/// </summary>
public static class AppEndpointExtensions
{
    /// <summary>Maps <c>/api/v1/apps</c> and everything beneath it.</summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    public static IEndpointRouteBuilder MapApps(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder apps = app.MapGroup("/api/v1/apps").WithTags("Applications");

        apps.MapGet("/", ManageApps.ListAsync)
            .RequireAuthorization(DlePolicies.AppsRead)
            .RequireRateLimiting(DleRateLimitPolicies.Read)
            .WithName("ListApps")
            .WithSummary("Lists the registered mobile applications.")
            .Produces<PagedResponse<AppResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        apps.MapGet("/{id:guid}", ManageApps.GetAsync)
            .RequireAuthorization(DlePolicies.AppsRead)
            .RequireRateLimiting(DleRateLimitPolicies.Read)
            .WithName("GetApp")
            .WithSummary("Reads one application.")
            .Produces<AppResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        apps.MapPost("/", ManageApps.CreateAsync)
            .RequireAuthorization(DlePolicies.AppsWrite)
            .RequireRateLimiting(DleRateLimitPolicies.Write)
            .WithIdempotency()
            .WithName("CreateApp")
            .WithSummary("Registers a mobile application.")
            .WithDescription(
                "iOS needs the Apple team identifier; Android needs the SHA-256 signing certificate "
                + "fingerprints. An Android registration whose fingerprints are not declared as Play "
                + "App Signing fingerprints is accepted, but the response carries a warning: an "
                + "upload certificate verifies in a debug build and silently fails for every install "
                + "from the Play Store.")
            .Produces<AppResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        apps.MapPatch("/{id:guid}", ManageApps.UpdateAsync)
            .RequireAuthorization(DlePolicies.AppsWrite)
            .RequireRateLimiting(DleRateLimitPolicies.Write)
            .WithIdempotency()
            .WithName("UpdateApp")
            .WithSummary("Changes an application.")
            .WithDescription(
                "Neither the platform nor the bundle identifier can be changed: both are part of the "
                + "identity the association file publishes. Declaring the Play App Signing "
                + "fingerprints here is what silences the upload-certificate warning.")
            .Produces<AppResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        apps.MapDelete("/{id:guid}", ManageApps.DeleteAsync)
            .RequireAuthorization(DlePolicies.AppsWrite)
            .RequireRateLimiting(DleRateLimitPolicies.Write)
            .WithIdempotency()
            .WithName("DeleteApp")
            .WithSummary("Removes an application.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        apps.MapGet("/{id:guid}/sdk-keys", AppSdkKeys.ListAsync)
            .RequireAuthorization(DlePolicies.AppsRead)
            .RequireRateLimiting(DleRateLimitPolicies.Read)
            .WithName("ListSdkKeys")
            .WithSummary("Lists the keys embedded in this application, without their secrets.")
            .Produces<PagedResponse<SdkKeyResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        apps.MapPost("/{id:guid}/sdk-keys", AppSdkKeys.CreateAsync)
            .RequireAuthorization(DlePolicies.AppsWrite)
            .RequireRateLimiting(DleRateLimitPolicies.Write)
            .WithIdempotency()
            .WithName("CreateSdkKey")
            .WithSummary("Issues a key for this application.")
            .WithDescription(
                "The secret appears in this response and nowhere else. The key authenticates only the "
                + "two SDK ingestion routes and can never write configuration, which is what makes it "
                + "safe to ship inside an application binary.")
            .Produces<SdkKeyCreatedResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        apps.MapDelete("/{id:guid}/sdk-keys/{keyId:guid}", AppSdkKeys.RevokeAsync)
            .RequireAuthorization(DlePolicies.AppsWrite)
            .RequireRateLimiting(DleRateLimitPolicies.Write)
            .WithIdempotency()
            .WithName("RevokeSdkKey")
            .WithSummary("Revokes a key embedded in this application.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }
}
