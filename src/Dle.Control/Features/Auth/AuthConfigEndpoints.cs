using Dle.Control.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Dle.Control.Features.Auth;

/// <summary>
/// Public authentication and OIDC discovery endpoints for the administration console (FR-242).
/// </summary>
public static class AuthConfigEndpoints
{
    /// <summary>
    /// Maps the public authentication configuration route.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapAuthConfig(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/v1/auth/config", (IOptions<DleIdentityOptions> options) =>
        {
            DleOidcOptions oidc = options.Value.Oidc;
            return TypedResults.Ok(new AuthConfigResponse
            {
                OidcEnabled = oidc.IsConfigured,
                Authority = oidc.IsConfigured ? oidc.Authority : null,
                ClientId = oidc.IsConfigured ? oidc.ClientId : null,
                Audience = oidc.IsConfigured ? oidc.Audience : null,
                Scopes = oidc.IsConfigured ? [.. oidc.Scopes] : [],
            });
        })
        .AllowAnonymous()
        .WithName("GetAuthConfig")
        .WithSummary("Reads public authentication and OIDC configuration for the administration console.")
        .WithTags("Auth")
        .Produces<AuthConfigResponse>()
        .ProducesProblem(StatusCodes.Status500InternalServerError);

        return app;
    }
}
