using Dle.Control.Configuration;
using Dle.Control.Features.ApiKeys;
using Dle.Control.Features.Shared;
using Dle.Control.Identity;

using Microsoft.AspNetCore.RateLimiting;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Maps the API key routes (SHARED-KERNEL §15, FR-242).
/// </summary>
public static class ApiKeyEndpointExtensions
{
    /// <summary>Maps <c>/api/v1/api-keys</c> and everything beneath it.</summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    public static IEndpointRouteBuilder MapApiKeys(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder keys = app.MapGroup("/api/v1/api-keys").WithTags("API keys");

        keys.MapGet("/", ManageApiKeys.ListAsync)
            .RequireAuthorization(DlePolicies.KeysRead)
            .RequireRateLimiting(DleRateLimitPolicies.Read)
            .WithName("ListApiKeys")
            .WithSummary("Lists the tenant's keys, without their secrets.")
            .WithDescription(
                "The last-used instant is here so that keys nobody uses can be found and retired.")
            .Produces<PagedResponse<ApiKeyResponse>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        keys.MapPost("/", ManageApiKeys.CreateAsync)
            .RequireAuthorization(DlePolicies.KeysWrite)
            .RequireRateLimiting(DleRateLimitPolicies.Write)
            .WithIdempotency()
            .WithName("CreateApiKey")
            .WithSummary("Issues a key. The secret is shown exactly once.")
            .WithDescription(
                "Only an Argon2id hash is stored, so a lost key is replaced, never recovered. A key "
                + "may not be issued with a role or a scope stronger than the credential issuing it.")
            .Produces<ApiKeyCreatedResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        keys.MapDelete("/{id:guid}", ManageApiKeys.RevokeAsync)
            .RequireAuthorization(DlePolicies.KeysWrite)
            .RequireRateLimiting(DleRateLimitPolicies.Write)
            .WithIdempotency()
            .WithName("RevokeApiKey")
            .WithSummary("Revokes a key.")
            .WithDescription(
                "The row is kept so that audit entries referring to it keep referring to something "
                + "that exists; it simply stops authenticating.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }
}
