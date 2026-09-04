using Dle.Domain.Attribution;

namespace Dle.Crypto;

/// <summary>
/// A freshly issued claim code: the six characters shown on the interstitial page, and the hash
/// that goes into <c>claim_codes.code_hash</c> (FR-184, K3).
/// </summary>
/// <remarks>
/// A class rather than a record, because <see cref="Hash"/> is an array and record equality over
/// an array compares references.
/// </remarks>
public sealed class ClaimCodeCredential
{
    /// <summary>The code, in the alphabet of <see cref="ClaimCode"/>. Shown to the user and never
    /// stored.</summary>
    public required string Code { get; init; }

    /// <summary>The keyed hash of <see cref="Code"/>, ready for the <c>bytea</c> column.</summary>
    public required byte[] Hash { get; init; }
}
