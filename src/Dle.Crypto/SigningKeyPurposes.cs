namespace Dle.Crypto;

/// <summary>
/// Values of <c>signing_keys.purpose</c>. A key is bound to one purpose so that compromising the
/// webhook key cannot be turned into the ability to mint click tokens.
/// </summary>
public static class SigningKeyPurposes
{
    /// <summary>Keys that sign tokens.</summary>
    public const string Token = "token";

    /// <summary>Keys that sign outbound webhook payloads.</summary>
    public const string Webhook = "webhook";

    /// <summary>Keys behind the click identifier permutation and its MAC.</summary>
    public const string ClickId = "click_id";

    /// <summary>Keys behind the slug permutation.</summary>
    public const string Slug = "slug";
}
