using Microsoft.AspNetCore.Authentication;

namespace Dle.Control.Identity;

/// <summary>
/// Options shared by the two key-based authentication schemes.
/// </summary>
public sealed class DleKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>The <c>Authorization</c> scheme a key is presented under.</summary>
    public const string BearerScheme = "Bearer";

    /// <summary>Header a control-plane key may be presented in instead of <c>Authorization</c>.</summary>
    public const string ApiKeyHeader = "X-Api-Key";

    /// <summary>Header an SDK key may be presented in instead of <c>Authorization</c>.</summary>
    public const string SdkKeyHeader = "X-Dle-Sdk-Key";

    /// <summary>
    /// The first field of a key of this kind, which is what tells the two apart on the wire.
    /// </summary>
    /// <remarks>
    /// A key looks like <c>&lt;name&gt;_&lt;prefix&gt;_&lt;secret&gt;</c>. The name field is not a
    /// secret and carries no entropy; it exists so that a credential can be routed to the right
    /// scheme without being verified first, and so that a key pasted into the wrong field fails with
    /// "wrong kind of key" rather than with a silent mismatch.
    /// </remarks>
    public string KeyName { get; set; } = "dle";

    /// <summary>Header this scheme reads when the credential is not in <c>Authorization</c>.</summary>
    public string HeaderName { get; set; } = ApiKeyHeader;
}
