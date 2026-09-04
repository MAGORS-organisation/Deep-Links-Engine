namespace Dle.Control.Identity;

/// <summary>
/// Names of the authentication schemes the control plane registers (FR-242).
/// </summary>
/// <remarks>
/// <para>
/// There are three credentials and they are three schemes, not one scheme with three code paths.
/// That separation is the mechanism behind §E.2.1 TB2: an SDK key ships inside an APK or an IPA and
/// must be assumed to be in an attacker's hands, so the policies that guard configuration writes
/// simply do not list <see cref="SdkKey"/> among their accepted schemes. No amount of scope or role
/// confusion can make an extracted SDK key satisfy them, because the authorization stack refuses the
/// scheme before it ever looks at a claim.
/// </para>
/// <para>
/// <see cref="Default"/> is a policy scheme that picks one of the three from the shape of the
/// presented credential. It exists so that an endpoint names a policy, never a scheme, and so that
/// a request carrying no credential at all is refused once, in one place.
/// </para>
/// </remarks>
public static class DleAuthenticationSchemes
{
    /// <summary>Selector scheme that forwards to whichever credential the request actually carries.</summary>
    public const string Default = "Dle";

    /// <summary>Control-plane API keys, hashed with Argon2id (FR-242, K5).</summary>
    public const string ApiKey = "DleApiKey";

    /// <summary>Keys embedded in customer applications. Narrow by construction (§E.2.1, TB2).</summary>
    public const string SdkKey = "DleSdkKey";

    /// <summary>Bearer tokens issued by the administration UI identity provider.</summary>
    public const string Oidc = "DleOidc";

    /// <summary>Interactive browser sign-in for the administration UI.</summary>
    public const string OidcInteractive = "DleOidcInteractive";

    /// <summary>Cookie that carries the interactive session.</summary>
    public const string OidcCookie = "DleOidcCookie";
}
