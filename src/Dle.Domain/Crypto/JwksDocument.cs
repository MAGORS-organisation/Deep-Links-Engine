namespace Dle.Domain.Crypto;

/// <summary>
/// The document served at <c>/.well-known/jwks.json</c>.
/// </summary>
/// <remarks>
/// The set holds every key a verifier may currently encounter, not only the current signer: during
/// a rotation the outgoing key stays published until the last signature it produced has expired,
/// otherwise rotation would invalidate signatures that are still legitimately in flight.
/// </remarks>
/// <param name="Keys">The published public keys.</param>
public sealed record JwksDocument(IReadOnlyList<JsonWebKey> Keys);
