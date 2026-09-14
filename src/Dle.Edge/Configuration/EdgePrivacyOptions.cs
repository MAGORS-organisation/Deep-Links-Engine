using System.ComponentModel.DataAnnotations;

namespace Dle.Edge.Configuration;

/// <summary>
/// How much of a client address the click stream is allowed to carry, bound from <c>Dle:Privacy</c>
/// (§C.4, §E.6.2, FR-247).
/// </summary>
/// <remarks>
/// <para>
/// This is the deployment-wide ceiling and the consent gate is the per-request one; the edge applies
/// the stricter of the two. A tenant that has full consent still stores nothing when the operator
/// configured <see cref="IpStorageModes.None"/>, and an operator who allows a prefix still stores
/// nothing for a visitor who did not consent to attribution. Neither is allowed to widen the other.
/// </para>
/// <para>
/// A raw address is never stored regardless of the setting: <see cref="ClickEvent"/> has no column
/// for one, so <see cref="IpStorageModes.Full"/> means the same as
/// <see cref="IpStorageModes.Prefix"/> here and exists only so the key is spelled the same way it is
/// in §C.4.
/// </para>
/// </remarks>
public sealed class EdgePrivacyOptions
{
    /// <summary>Configuration section this type binds to.</summary>
    public const string SectionName = "Dle:Privacy";

    /// <summary>
    /// One of <c>none</c>, <c>hash_only</c>, <c>prefix</c> or <c>full</c>.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^(none|hash_only|prefix|full)$")]
    public string IpStorage { get; set; } = IpStorageModes.HashOnly;

    /// <summary>Whether a keyed hash of the address may be written.</summary>
    public bool AllowsIpHash => !string.Equals(IpStorage, IpStorageModes.None, StringComparison.Ordinal);

    /// <summary>Whether the network prefix of the address may be written.</summary>
    public bool AllowsIpPrefix =>
        string.Equals(IpStorage, IpStorageModes.Prefix, StringComparison.Ordinal) ||
        string.Equals(IpStorage, IpStorageModes.Full, StringComparison.Ordinal);
}

/// <summary>Permitted values of <see cref="EdgePrivacyOptions.IpStorage"/>.</summary>
public static class IpStorageModes
{
    /// <summary>Nothing derived from the client address is written.</summary>
    public const string None = "none";

    /// <summary>Only the daily-rotated keyed hash is written.</summary>
    public const string HashOnly = "hash_only";

    /// <summary>The keyed hash and the /24 or /48 network prefix are written.</summary>
    public const string Prefix = "prefix";

    /// <summary>
    /// Same effect as <see cref="Prefix"/>: the click stream has no column for a raw address.
    /// </summary>
    public const string Full = "full";
}
