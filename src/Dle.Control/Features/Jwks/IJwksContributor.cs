using Dle.Domain.Crypto;

namespace Dle.Control.Features.Jwks;

/// <summary>
/// A source of public keys that are published at <c>/.well-known/jwks.json</c> alongside the ones
/// the key ring holds.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IKeyRing"/> owns the token signing keys of §E.4.2, and it is deliberately not the
/// owner of every key in the product: a webhook is signed with a key bound to the webhook purpose,
/// so that compromising it cannot be turned into the ability to mint click tokens (§B.5.2). Those
/// keys still have to be published, because the whole point of the asymmetric half of the webhook
/// signature is that a third party can verify it against the key set without holding the shared
/// secret.
/// </para>
/// <para>
/// The seam exists so that the JWKS endpoint does not have to know which modules have keys. A
/// module registers a contributor when it is composed, and a deployment that leaves the module out
/// simply publishes fewer keys.
/// </para>
/// </remarks>
public interface IJwksContributor
{
    /// <summary>
    /// The keys this module wants published, at the instant given.
    /// </summary>
    /// <param name="asOf">The instant the key set is being built for.</param>
    /// <returns>
    /// Every key that is still inside its validity window, retired ones included. A key that has
    /// stopped signing must stay published until it stops being accepted, or rotation would break
    /// verification of a payload sent a second before the rotation (S-12).
    /// </returns>
    IReadOnlyList<JsonWebKey> GetKeys(DateTimeOffset asOf);
}
