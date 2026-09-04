using System.ComponentModel.DataAnnotations;

namespace Dle.Control.Configuration;

/// <summary>
/// Authentication configuration bound from the <c>Dle:Identity</c> section (FR-242).
/// </summary>
public sealed class DleIdentityOptions
{
    /// <summary>Configuration section this class binds to.</summary>
    public const string SectionName = "Dle:Identity";

    /// <summary>
    /// How long a successful credential verification stays cached, in seconds.
    /// </summary>
    /// <remarks>
    /// Argon2id is deliberately expensive — that is what makes a stolen database useless — so
    /// paying it on every request would put tens of milliseconds on every call. The cache holds
    /// only the fact that a given presented value verified against a given key row, never the key
    /// itself, and the window is short enough that a revocation takes effect within it. Zero
    /// disables the cache and pays the full cost per request.
    /// </remarks>
    [Range(0, 3600)]
    public int CredentialCacheSeconds { get; set; } = 60;

    /// <summary>
    /// Whether keys shipped inside customer applications may authenticate at all.
    /// </summary>
    /// <remarks>
    /// A deployment with no mobile SDK in the field can switch the whole scheme off. It is a
    /// separate switch from the control-plane key scheme on purpose: the two credentials share
    /// nothing but the transport (§E.2.1, TB2).
    /// </remarks>
    public bool EnableSdkKeys { get; set; } = true;

    /// <summary>
    /// First field of an SDK key, which is what routes it to the SDK authentication scheme.
    /// </summary>
    /// <remarks>
    /// It must differ from <c>Dle:Crypto:ApiKeyPrefix</c>. If the two matched, a control-plane key
    /// and an application key would be indistinguishable before verification, and the scheme
    /// separation that §E.2.1 TB2 rests on would collapse into a lookup order. The composition root
    /// refuses to start when they are equal.
    /// </remarks>
    [Required]
    [RegularExpression("^[a-z0-9]{2,16}$")]
    public string SdkKeyName { get; set; } = "dlk";

    /// <summary>OpenID Connect settings for the administration UI. Optional.</summary>
    [Required]
    public DleOidcOptions Oidc { get; set; } = new();
}

/// <summary>
/// OpenID Connect settings for the administration UI (FR-242).
/// </summary>
/// <remarks>
/// The whole block is optional. An API-only, self-hosted deployment authenticates with API keys and
/// never registers an interactive scheme; leaving <see cref="Authority"/> empty is the supported way
/// to say so and is not a validation failure.
/// </remarks>
public sealed class DleOidcOptions
{
    /// <summary>Issuer URL of the identity provider. Empty disables every OIDC scheme.</summary>
    public string? Authority { get; set; }

    /// <summary>Client identifier registered with the identity provider.</summary>
    public string? ClientId { get; set; }

    /// <summary>Client secret for the authorization code flow.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Audience the access tokens carry, when it differs from the client identifier.</summary>
    public string? Audience { get; set; }

    /// <summary>Additional scopes requested during interactive sign in.</summary>
    public IList<string> Scopes { get; } = ["openid", "profile", "email"];

    /// <summary>
    /// Claim carrying the tenant the signed-in operator administers.
    /// </summary>
    /// <remarks>
    /// Without it a token from the identity provider cannot establish a tenant, and the request is
    /// refused rather than served against an arbitrary one (SHARED-KERNEL §17.9).
    /// </remarks>
    [Required]
    [StringLength(128, MinimumLength = 1)]
    public string TenantClaim { get; set; } = "dle_tenant";

    /// <summary>Claim carrying the role of the signed-in operator.</summary>
    [Required]
    [StringLength(128, MinimumLength = 1)]
    public string RoleClaim { get; set; } = "dle_role";

    /// <summary>
    /// Whether metadata retrieval requires HTTPS. Only ever turned off against a local provider.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>Whether an authority has been configured at all.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Authority);
}
