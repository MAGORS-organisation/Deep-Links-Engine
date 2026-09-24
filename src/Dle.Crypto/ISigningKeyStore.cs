namespace Dle.Crypto;

/// <summary>
/// Durable home of the signing keys. The seam between this module and the <c>signing_keys</c>
/// table (§B.5.2).
/// </summary>
/// <remarks>
/// <para>
/// This project ships the configuration backed implementation and nothing else, on purpose:
/// <c>Dle.Crypto</c> must not know about EF Core or Npgsql. A deployment that rotates keys in the
/// database registers its own implementation before calling
/// <see cref="Microsoft.Extensions.DependencyInjection.CryptoServiceCollectionExtensions.AddDleCrypto"/>,
/// and the key ring picks it up
/// without a code change here.
/// </para>
/// <para>
/// Loading is asynchronous, which is why <see cref="KeyRing"/> never depends on it to produce its
/// first signer: a key ring that could not sign until a database answered would make the control
/// plane fail to start whenever the database is slow.
/// </para>
/// </remarks>
public interface ISigningKeyStore
{
    /// <summary>Loads every key stored for a purpose, retired ones included.</summary>
    /// <param name="purpose">Purpose, one of the constants on <see cref="SigningKeyPurposes"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The keys. Retired keys are part of the answer, because a signature made under one
    /// has to keep verifying until its window closes (S-12).</returns>
    ValueTask<IReadOnlyList<SigningKeyMaterial>> LoadAsync(string purpose, CancellationToken ct);

    /// <summary>Stores a newly generated key and makes it the current one for its purpose.</summary>
    /// <param name="material">The key, private half included.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the key is durable.</returns>
    ValueTask SaveAsync(SigningKeyMaterial material, CancellationToken ct);

    /// <summary>
    /// Stops a key from signing while keeping it acceptable for verification.
    /// </summary>
    /// <param name="kid">Identifier of the key to retire.</param>
    /// <param name="notAfter">Instant after which the key stops being accepted. Always in the
    /// future: a retirement that closed the window immediately would invalidate signatures made a
    /// moment earlier, which is exactly what S-12 forbids.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the change is durable.</returns>
    ValueTask RetireAsync(string kid, DateTimeOffset notAfter, CancellationToken ct);
}
