namespace Dle.Control.Features.Auth;

/// <summary>
/// Public authentication and OIDC configuration for the administration UI (FR-242).
/// </summary>
public sealed class AuthConfigResponse
{
    /// <summary>Whether an OpenID Connect provider is configured for SSO.</summary>
    public bool OidcEnabled { get; set; }

    /// <summary>Issuer / Authority URL of the identity provider, if configured.</summary>
    public string? Authority { get; set; }

    /// <summary>Client identifier for interactive PKCE login, if configured.</summary>
    public string? ClientId { get; set; }

    /// <summary>Token audience expected by the API, if configured.</summary>
    public string? Audience { get; set; }

    /// <summary>Scopes to request during interactive sign in.</summary>
    public IList<string> Scopes { get; set; } = [];
}
