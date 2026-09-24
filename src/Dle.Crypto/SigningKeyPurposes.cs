namespace Dle.Crypto;

/// <summary>
/// Values of <c>signing_keys.purpose</c>. A key is bound to one purpose so that compromising the
/// webhook key cannot be turned into the ability to mint click tokens (§B.5.2, §E.4.2).
/// </summary>
public static class SigningKeyPurposes
{
    /// <summary>Keys that sign the tokens described in §E.4.2.</summary>
    public const string Token = "token";

    /// <summary>Keys that sign outbound webhook payloads (K4).</summary>
    public const string Webhook = "webhook";

    /// <summary>Keys behind the click identifier permutation and its MAC (K2).</summary>
    public const string ClickId = "click_id";

    /// <summary>Keys behind the slug permutation (ADR-007).</summary>
    public const string Slug = "slug";
}
