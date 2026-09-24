using System.Text;

namespace Dle.Crypto;

/// <summary>
/// Expands the configured master secret into the independent keys the module needs.
/// </summary>
/// <remarks>
/// HKDF-SHA-256 with a per purpose <c>info</c> label. Using one secret directly for several
/// primitives would mean that a key recovered from one of them — say, through a padding oracle in
/// a future feature — is immediately usable against all of them. The labels are part of the
/// on-disk contract: changing one changes every value derived under it, so they carry an explicit
/// version suffix.
/// </remarks>
internal static class CryptoKeyDerivation
{
    /// <summary>Length of every derived key, in bytes.</summary>
    internal const int KeyLength = 32;

    /// <summary>Label for the slug permutation key (ADR-007).</summary>
    internal const string SlugLabel = "dle:slug:feistel:v1";

    /// <summary>Label for the click identifier permutation key (§B.6.3).</summary>
    internal const string ClickIdPermutationLabel = "dle:click-id:permutation:v1";

    /// <summary>Label for the click identifier MAC key (T-05).</summary>
    internal const string ClickIdMacLabel = "dle:click-id:mac:v1";

    /// <summary>Label for the IP hash secret (K9).</summary>
    internal const string IpHashLabel = "dle:ip-hash:v1";

    /// <summary>Label for the claim code pepper (K3).</summary>
    internal const string ClaimCodeLabel = "dle:claim-code:v1";

    /// <summary>Label prefix for the derived bootstrap signing key. The algorithm identifier is
    /// appended, so changing the algorithm changes the key rather than reusing one.</summary>
    internal const string SigningKeyLabel = "dle:signing:key:v1";

    /// <summary>Label prefix for the identifier of the derived bootstrap signing key.</summary>
    internal const string SigningKidLabel = "dle:signing:kid:v1";

    /// <summary>Fixed salt. A constant salt is sound for HKDF; the entropy is in the secret.</summary>
    private static readonly byte[] Salt = "dle:crypto:hkdf:v1"u8.ToArray();

    /// <summary>
    /// Derives a key for one purpose.
    /// </summary>
    /// <param name="masterSecret">The configured master secret.</param>
    /// <param name="overrideSecret">A purpose specific secret that replaces
    /// <paramref name="masterSecret"/> as the input keying material, or <see langword="null"/>.</param>
    /// <param name="label">The purpose label.</param>
    /// <param name="length">Length of the derived key in bytes.</param>
    /// <returns>The derived key.</returns>
    internal static byte[] Derive(string masterSecret, string? overrideSecret, string label, int length = KeyLength)
    {
        string source = string.IsNullOrEmpty(overrideSecret) ? masterSecret : overrideSecret;
        byte[] ikm = Encoding.UTF8.GetBytes(source);

        try
        {
            return HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                ikm,
                length,
                Salt,
                Encoding.UTF8.GetBytes(label));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ikm);
        }
    }
}
