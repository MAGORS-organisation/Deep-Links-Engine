using System.ComponentModel.DataAnnotations;

namespace Dle.Crypto;

/// <summary>
/// Configuration of the cryptography module, bound from the <c>Dle:Crypto</c> section (§C.4,
/// SHARED-KERNEL §16).
/// </summary>
/// <remarks>
/// <para>
/// One secret is mandatory: <see cref="MasterSecret"/>. Every keyed primitive in the module — the
/// slug permutation, the click identifier permutation and its MAC, the IP hash salt, the claim
/// code pepper and the bootstrap signing key — is an independent HKDF-SHA-256 expansion of it
/// under a distinct label, so learning one derived key tells an attacker nothing about the others
/// and a small deployment has exactly one secret to manage.
/// </para>
/// <para>
/// Changing <see cref="MasterSecret"/> changes the slug permutation. Slugs already issued keep
/// resolving, because they are stored, but <see cref="ISlugGenerator.TryRecoverSequence"/> stops
/// reversing them and newly issued slugs may collide with old ones on the unique index. Set
/// <see cref="SlugSecret"/> explicitly if the master secret is ever going to be rotated.
/// </para>
/// </remarks>
public sealed class CryptoOptions
{
    /// <summary>Configuration section this type binds to.</summary>
    public const string SectionName = "Dle:Crypto";

    /// <summary>
    /// The single secret every other key is derived from. Supply at least 32 characters of high
    /// entropy through an environment variable or a secret store, never through a committed
    /// <c>appsettings.json</c>.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [MinLength(32)]
    public string MasterSecret { get; set; } = string.Empty;

    /// <summary>Overrides the derived slug permutation key, so that it can be rotated
    /// independently of <see cref="MasterSecret"/>. Minimum 32 characters.</summary>
    [MinLength(32)]
    public string? SlugSecret { get; set; }

    /// <summary>Overrides the derived click identifier key. Minimum 32 characters.</summary>
    [MinLength(32)]
    public string? ClickIdSecret { get; set; }

    /// <summary>Overrides the derived IP hash secret (K9). Minimum 32 characters.</summary>
    [MinLength(32)]
    public string? IpHashSecret { get; set; }

    /// <summary>Overrides the derived claim code pepper (K3). Minimum 32 characters.</summary>
    [MinLength(32)]
    public string? ClaimCodeSecret { get; set; }

    /// <summary>Algorithm the signer uses. Exactly one, unlike
    /// <see cref="AcceptedAlgorithms"/> (§E.4.2).</summary>
    [Required(AllowEmptyStrings = false)]
    public string SigningAlgorithm { get; set; } = SignatureAlgorithms.Hs256;

    /// <summary>
    /// Algorithms the verifier will consider. <see cref="SigningAlgorithm"/> is always included.
    /// Anything outside this set is refused before a signature is even attempted, which is what
    /// prevents an attacker from downgrading a token by rewriting its <c>alg</c> segment.
    /// </summary>
    public IList<string> AcceptedAlgorithms { get; } = [];

    /// <summary>
    /// Enables the post-quantum algorithms (<see cref="SignatureAlgorithms.MlDsa65"/>,
    /// <see cref="SignatureAlgorithms.Ed25519MlDsa65"/> and
    /// <see cref="SignatureAlgorithms.SlhDsa128s"/>). Off by default: §E.5.3 puts them in phase 1,
    /// and phase 0 ships only the agility that makes adopting them cheap.
    /// </summary>
    public bool HybridPqEnabled { get; set; }

    /// <summary>Age at which a signing key is replaced.</summary>
    [Range(1, 3650)]
    public int KeyRotationDays { get; set; } = 90;

    /// <summary>
    /// How long a retired key stays acceptable for verification after it stops signing. Rotation
    /// must never invalidate a signature made with a key still inside its window (S-12), so this
    /// has to exceed the lifetime of the longest lived artefact the key signed.
    /// </summary>
    [Range(1, 365)]
    public int KeyOverlapDays { get; set; } = 7;

    /// <summary>Number of Feistel rounds behind the slug and click identifier permutations
    /// (ADR-007 specifies four).</summary>
    [Range(2, 32)]
    public int SlugFeistelRounds { get; set; } = FeistelPermutation.DefaultRounds;

    /// <summary>How often the IP hash salt rotates, in hours. Daily by default (K9, FR-247):
    /// the rotation is what bounds how long two events stay correlatable.</summary>
    [Range(1, 8760)]
    public int IpSaltRotationHours { get; set; } = 24;

    /// <summary>Default <c>aud</c> claim written into issued tokens.</summary>
    [Required(AllowEmptyStrings = false)]
    public string TokenAudience { get; set; } = "dle";

    /// <summary>Default token lifetime in seconds.</summary>
    [Range(5, 86400)]
    public int TokenLifetimeSeconds { get; set; } = 300;

    /// <summary>Tolerance applied to <c>iat</c> and <c>exp</c> when validating, in seconds.</summary>
    [Range(0, 3600)]
    public int TokenClockSkewSeconds { get; set; } = 60;

    /// <summary>Number of single use token identifiers the in-memory replay guard remembers.</summary>
    [Range(1024, 10_000_000)]
    public int ReplayGuardCapacity { get; set; } = 100_000;

    /// <summary>Human readable prefix of a generated API key, so that a leaked key is
    /// recognisable in a log or a paste (for example <c>dle</c> in <c>dle_a1B2c3D4_…</c>).</summary>
    [Required(AllowEmptyStrings = false)]
    [RegularExpression("^[a-z][a-z0-9]{1,15}$")]
    public string ApiKeyPrefix { get; set; } = "dle";

    /// <summary>Cost parameters of the API key hash.</summary>
    public Argon2Options Argon2 { get; set; } = new();

    /// <summary>Signing keys supplied through configuration.</summary>
    public IList<SigningKeyOptions> Keys { get; } = [];
}
