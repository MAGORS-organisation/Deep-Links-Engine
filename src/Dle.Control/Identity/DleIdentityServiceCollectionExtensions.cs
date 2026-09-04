using Dle.Control.Configuration;
using Dle.Control.Features.Shared;
using Dle.Control.Identity;
using Dle.Crypto;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The single composition entry point of the identity module (SHARED-KERNEL §15, FR-242).
/// </summary>
/// <remarks>
/// <para>
/// Three credentials, three authentication schemes. That separation is the mechanism behind §E.2.1
/// TB2: an SDK key ships inside an APK or an IPA and must be assumed to be in an attacker's hands,
/// so the policies that guard configuration writes simply do not list the SDK scheme among the
/// schemes they accept. No confusion of roles or scopes can make an extracted SDK key satisfy them,
/// because the authorization stack refuses the scheme before it ever looks at a claim.
/// </para>
/// <para>
/// The default is deny. The fallback policy is one nothing satisfies, so an endpoint registered
/// without <c>RequireAuthorization</c> refuses the request instead of serving it — a forgotten
/// attribute has to fail closed (SHARED-KERNEL §17.9).
/// </para>
/// </remarks>
public static class DleIdentityServiceCollectionExtensions
{
    /// <summary>
    /// Registers the authentication schemes, the authorization policies and the credential services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">
    /// Application configuration, read for <c>Dle:Identity</c> and for
    /// <c>Dle:Crypto:ApiKeyPrefix</c>, which is the name field a control-plane key carries.
    /// </param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The SDK key name and the control-plane key name are equal, which would make the two
    /// credentials indistinguishable before verification and collapse the scheme separation §E.2.1
    /// TB2 rests on into a lookup order.
    /// </exception>
    public static IServiceCollection AddDleIdentity(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        IConfigurationSection section = configuration.GetSection(DleIdentityOptions.SectionName);

        services.AddOptions<DleIdentityOptions>()
            .Bind(section)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        DleIdentityOptions identity = section.Get<DleIdentityOptions>() ?? new DleIdentityOptions();

        string apiKeyName =
            configuration[$"{CryptoOptions.SectionName}:ApiKeyPrefix"]?.Trim() ?? "dle";

        if (string.Equals(apiKeyName, identity.SdkKeyName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Dle:Identity:SdkKeyName must differ from Dle:Crypto:ApiKeyPrefix. If the two "
                + "matched, a control-plane key and an application key would be indistinguishable "
                + "before verification, and the scheme separation of §E.2.1 TB2 would become a "
                + "lookup order.");
        }

        RegisterServices(services);
        RegisterAuthentication(services, identity, apiKeyName);
        RegisterAuthorization(services, identity);

        return services;
    }

    /// <summary>Registers the credential lookup, cache, limiter and factories.</summary>
    /// <param name="services">The service collection.</param>
    private static void RegisterServices(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        // Scoped: it reads through the request's DbContext. Everything else here holds process-wide
        // state and is a singleton on purpose — the attempt limiter and the verification cache would
        // both be useless per request.
        services.TryAddScoped<DleCredentialStore>();

        services.TryAddSingleton<DleCredentialCache>();
        services.TryAddSingleton<DleAuthAttemptLimiter>();
        services.TryAddSingleton<DleDecoyCredential>();
        services.TryAddSingleton<DleSdkKeyFactory>();

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAuthorizationHandler, DleRoleAuthorizationHandler>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAuthorizationHandler, DleScopeAuthorizationHandler>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAuthorizationHandler, DleInstanceOperatorHandler>());
    }

    /// <summary>Registers the three authentication schemes and the selector in front of them.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="identity">The bound identity options.</param>
    /// <param name="apiKeyName">First field of a control-plane key.</param>
    private static void RegisterAuthentication(
        IServiceCollection services,
        DleIdentityOptions identity,
        string apiKeyName)
    {
        AuthenticationBuilder authentication = services
            .AddAuthentication(DleAuthenticationSchemes.Default)
            .AddPolicyScheme(
                DleAuthenticationSchemes.Default,
                DleAuthenticationSchemes.Default,
                options => options.ForwardDefaultSelector =
                    httpContext => SelectScheme(httpContext, identity, apiKeyName));

        authentication.AddScheme<DleKeyAuthenticationOptions, DleApiKeyAuthenticationHandler>(
            DleAuthenticationSchemes.ApiKey,
            options =>
            {
                options.KeyName = apiKeyName;
                options.HeaderName = DleKeyAuthenticationOptions.ApiKeyHeader;
            });

        if (identity.EnableSdkKeys)
        {
            authentication.AddScheme<DleKeyAuthenticationOptions, DleSdkKeyAuthenticationHandler>(
                DleAuthenticationSchemes.SdkKey,
                options =>
                {
                    options.KeyName = identity.SdkKeyName;
                    options.HeaderName = DleKeyAuthenticationOptions.SdkKeyHeader;
                });
        }

        if (identity.Oidc.IsConfigured)
        {
            authentication.AddJwtBearer(DleAuthenticationSchemes.Oidc, options =>
            {
                options.Authority = identity.Oidc.Authority;
                options.RequireHttpsMetadata = identity.Oidc.RequireHttpsMetadata;
                options.MapInboundClaims = false;

                if (!string.IsNullOrWhiteSpace(identity.Oidc.Audience))
                {
                    options.Audience = identity.Oidc.Audience;
                }

                // The tenant and the role arrive as claims from the identity provider. Their names
                // are configuration because no two providers agree on them, and a token without the
                // tenant claim establishes no tenant at all — which the caller reader turns into a
                // refusal rather than into a request scoped to an arbitrary tenant.
                options.TokenValidationParameters.NameClaimType = "sub";
                options.TokenValidationParameters.RoleClaimType = identity.Oidc.RoleClaim;
                options.TokenValidationParameters.ValidateIssuer = true;
                options.TokenValidationParameters.ValidateLifetime = true;
            });
        }
    }

    /// <summary>
    /// Chooses which scheme a request's credential belongs to, without verifying it.
    /// </summary>
    /// <param name="httpContext">The request.</param>
    /// <param name="identity">The bound identity options.</param>
    /// <param name="apiKeyName">First field of a control-plane key.</param>
    /// <returns>The scheme to forward to.</returns>
    /// <remarks>
    /// Shape parsing is not authentication: everything decided here comes from bytes the caller
    /// supplied, and a value routed to a scheme still has to survive that scheme's verification. What
    /// the name field buys is that a key pasted into the wrong place fails with "wrong kind of key"
    /// rather than being tried against both stores in turn — which would be an oracle for which kind
    /// of credential a given prefix belongs to.
    /// </remarks>
    private static string SelectScheme(
        HttpContext httpContext,
        DleIdentityOptions identity,
        string apiKeyName)
    {
        if (identity.EnableSdkKeys
            && httpContext.Request.Headers.ContainsKey(DleKeyAuthenticationOptions.SdkKeyHeader))
        {
            return DleAuthenticationSchemes.SdkKey;
        }

        if (httpContext.Request.Headers.ContainsKey(DleKeyAuthenticationOptions.ApiKeyHeader))
        {
            return DleAuthenticationSchemes.ApiKey;
        }

        string? presented = DleKeyCredentialReader.Read(
            httpContext.Request,
            DleKeyAuthenticationOptions.ApiKeyHeader);

        if (presented is not null)
        {
            if (identity.EnableSdkKeys
                && DleKeyCredentialReader.TryReadPrefix(presented, identity.SdkKeyName, out _))
            {
                return DleAuthenticationSchemes.SdkKey;
            }

            if (DleKeyCredentialReader.TryReadPrefix(presented, apiKeyName, out _))
            {
                return DleAuthenticationSchemes.ApiKey;
            }
        }

        // Neither key shape matched. A configured identity provider gets the token; without one the
        // request goes to the control-plane key scheme, which refuses it in constant time like any
        // other unusable credential.
        return identity.Oidc.IsConfigured
            ? DleAuthenticationSchemes.Oidc
            : DleAuthenticationSchemes.ApiKey;
    }

    /// <summary>Registers the authorization policies and the deny-by-default fallback.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="identity">The bound identity options.</param>
    private static void RegisterAuthorization(IServiceCollection services, DleIdentityOptions identity)
    {
        string[] controlSchemes = identity.Oidc.IsConfigured
            ? [DleAuthenticationSchemes.ApiKey, DleAuthenticationSchemes.Oidc]
            : [DleAuthenticationSchemes.ApiKey];

        AuthorizationBuilder builder = services.AddAuthorizationBuilder();

        AddControlPolicy(builder, controlSchemes, DlePolicies.LinksRead, DleRoles.Viewer, DleScopes.LinksRead);
        AddControlPolicy(builder, controlSchemes, DlePolicies.LinksWrite, DleRoles.Editor, DleScopes.LinksWrite);
        AddControlPolicy(builder, controlSchemes, DlePolicies.DomainsRead, DleRoles.Viewer, DleScopes.DomainsRead);
        AddControlPolicy(builder, controlSchemes, DlePolicies.DomainsWrite, DleRoles.Admin, DleScopes.DomainsWrite);
        AddControlPolicy(builder, controlSchemes, DlePolicies.AppsRead, DleRoles.Viewer, DleScopes.AppsRead);
        AddControlPolicy(builder, controlSchemes, DlePolicies.AppsWrite, DleRoles.Admin, DleScopes.AppsWrite);
        AddControlPolicy(builder, controlSchemes, DlePolicies.TenantsRead, DleRoles.Viewer, DleScopes.TenantsRead);
        AddControlPolicy(builder, controlSchemes, DlePolicies.KeysRead, DleRoles.Admin, DleScopes.KeysRead);
        AddControlPolicy(builder, controlSchemes, DlePolicies.KeysWrite, DleRoles.Owner, DleScopes.KeysWrite);
        AddControlPolicy(builder, controlSchemes, DlePolicies.AnalyticsRead, DleRoles.Viewer, DleScopes.AnalyticsRead);

        // Tenant management additionally requires the caller to be recognised as the instance
        // operator: every tenant has an owner, so the owner role alone cannot be the gate.
        builder.AddPolicy(DlePolicies.TenantsWrite, policy => policy
            .AddAuthenticationSchemes(controlSchemes)
            .RequireAuthenticatedUser()
            .AddRequirements(
                new DleRoleRequirement(DleRoles.Owner),
                new DleScopeRequirement(DleScopes.TenantsWrite),
                new DleInstanceOperatorRequirement()));

        // The only policy that accepts the SDK scheme, and the only policy the SDK scheme can
        // satisfy (§E.2.1, TB2).
        builder.AddPolicy(DlePolicies.SdkIngest, policy => policy
            .AddAuthenticationSchemes(DleAuthenticationSchemes.SdkKey)
            .RequireAuthenticatedUser()
            .AddRequirements(new DleScopeRequirement(DleScopes.SdkIngest)));

        builder.AddPolicy(DlePolicies.Deny, policy => policy.RequireAssertion(static _ => false));

        // Deny by default. An endpoint that named no policy is refused rather than served, so a
        // forgotten RequireAuthorization is a broken endpoint and not an open one.
        builder.SetFallbackPolicy(new AuthorizationPolicyBuilder()
            .RequireAssertion(static _ => false)
            .Build());
    }

    /// <summary>Adds one control-plane policy: a role floor and a scope, together.</summary>
    /// <param name="builder">The authorization builder.</param>
    /// <param name="schemes">Schemes the policy accepts. The SDK scheme is never among them.</param>
    /// <param name="policyName">Name of the policy.</param>
    /// <param name="minimumRole">Weakest role that satisfies it.</param>
    /// <param name="scope">Scope the operation needs.</param>
    /// <remarks>
    /// Role and scope are an AND, never an OR. The role sets the ceiling of what a credential may
    /// ever do; the scope list, when the key carries one, lowers it further. Both are checked.
    /// </remarks>
    private static void AddControlPolicy(
        AuthorizationBuilder builder,
        string[] schemes,
        string policyName,
        string minimumRole,
        string scope) =>
        builder.AddPolicy(policyName, policy => policy
            .AddAuthenticationSchemes(schemes)
            .RequireAuthenticatedUser()
            .AddRequirements(
                new DleRoleRequirement(minimumRole),
                new DleScopeRequirement(scope)));
}
