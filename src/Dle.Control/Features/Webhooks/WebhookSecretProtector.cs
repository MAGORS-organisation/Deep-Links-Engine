using System.Security.Cryptography;
using System.Text;

using Dle.Crypto;

using Microsoft.Extensions.Options;

namespace Dle.Control.Features.Webhooks;

/// <summary>
/// Encrypts and decrypts the shared secret stored in
/// <see cref="Dle.Domain.Entities.WebhookSubscription.SecretEncrypted"/> (§B.5.2, §E.4.1 K4).
/// </summary>
/// <remarks>
/// <para>
/// The column is named <c>secret_encrypted</c> in the data model and this is what makes that name
/// true. A shared secret cannot be hashed the way a credential is: the engine has to reproduce it
/// on every delivery in order to compute the HMAC, so it is encrypted at rest and decrypted in the
/// process. What that buys is bounded and worth stating honestly — a database backup, a replica, a
/// dump handed to a support engineer, or a SQL injection that reads rows all yield ciphertext, and
/// the key is not in the database. It does not protect against an attacker who already runs code
/// in this process.
/// </para>
/// <para>
/// AES-256-GCM with a random 96 bit nonce per encryption, the key derived from
/// <see cref="CryptoOptions.MasterSecret"/> under a webhook specific HKDF label. The stored layout
/// is <c>version(1) | nonce(12) | tag(16) | ciphertext</c>; the version byte exists so that a
/// future scheme can be introduced without a migration that has to guess what a row holds.
/// </para>
/// </remarks>
public sealed class WebhookSecretProtector
{
    /// <summary>Length of a generated subscription secret, in bytes.</summary>
    /// <remarks>
    /// Thirty-two bytes, so the HMAC key is at least as long as the SHA-256 output it produces.
    /// Anything shorter would make the key the weakest part of a construction chosen for strength.
    /// </remarks>
    public const int SecretLengthBytes = 32;

    /// <summary>Version byte of the current storage layout.</summary>
    private const byte Version1 = 1;

    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 1 + NonceSize + TagSize;

    /// <summary>HKDF label of the key that wraps subscription secrets.</summary>
    private const string KeyLabel = "dle:webhook:secret:v1";

    private static readonly byte[] Salt = "dle:webhook:hkdf:v1"u8.ToArray();

    private readonly byte[] _key;

    /// <summary>
    /// Creates the protector and derives its key.
    /// </summary>
    /// <param name="crypto">Crypto options, read for the master secret.</param>
    /// <exception cref="ArgumentNullException"><paramref name="crypto"/> is <see langword="null"/>.</exception>
    public WebhookSecretProtector(IOptions<CryptoOptions> crypto)
    {
        ArgumentNullException.ThrowIfNull(crypto);

        byte[] ikm = Encoding.UTF8.GetBytes(crypto.Value.MasterSecret);

        try
        {
            _key = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                ikm,
                32,
                Salt,
                Encoding.UTF8.GetBytes(KeyLabel));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ikm);
        }
    }

    /// <summary>Generates a fresh subscription secret.</summary>
    /// <returns>Cryptographically random bytes, to be shown to the customer exactly once.</returns>
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretLengthBytes);

    /// <summary>Renders a secret in the form the customer stores.</summary>
    /// <param name="secret">The raw secret.</param>
    /// <returns>The base64 form, which is what the customer feeds to their HMAC implementation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="secret"/> is <see langword="null"/>.</exception>
    public static string Render(byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        return Convert.ToBase64String(secret);
    }

    /// <summary>
    /// Encrypts a secret for storage.
    /// </summary>
    /// <param name="secret">The raw secret.</param>
    /// <returns>The stored representation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="secret"/> is <see langword="null"/>.</exception>
    public byte[] Protect(byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);

        byte[] stored = new byte[HeaderSize + secret.Length];
        stored[0] = Version1;

        Span<byte> nonce = stored.AsSpan(1, NonceSize);
        Span<byte> tag = stored.AsSpan(1 + NonceSize, TagSize);
        Span<byte> ciphertext = stored.AsSpan(HeaderSize);

        RandomNumberGenerator.Fill(nonce);

        using AesGcm aes = new(_key, TagSize);
        aes.Encrypt(nonce, secret, ciphertext, tag);

        return stored;
    }

    /// <summary>
    /// Decrypts a stored secret.
    /// </summary>
    /// <param name="stored">The stored representation.</param>
    /// <param name="secret">The raw secret when it could be recovered.</param>
    /// <returns><see langword="false"/> when the row is malformed, was written under a different
    /// key, or has been tampered with.</returns>
    /// <remarks>
    /// Failure is reported rather than thrown, and the caller's answer to it is to record the
    /// delivery as failed. A subscription whose secret cannot be decrypted must never be delivered
    /// to with an unsigned or differently signed request: the receiver would either reject it or,
    /// worse, accept it (SHARED-KERNEL §17.9).
    /// </remarks>
    public bool TryUnprotect(byte[]? stored, out byte[] secret)
    {
        secret = [];

        if (stored is null || stored.Length <= HeaderSize || stored[0] != Version1)
        {
            return false;
        }

        ReadOnlySpan<byte> nonce = stored.AsSpan(1, NonceSize);
        ReadOnlySpan<byte> tag = stored.AsSpan(1 + NonceSize, TagSize);
        ReadOnlySpan<byte> ciphertext = stored.AsSpan(HeaderSize);

        byte[] plaintext = new byte[ciphertext.Length];

        try
        {
            using AesGcm aes = new(_key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
        }
        catch (CryptographicException)
        {
            // Authentication failed: wrong key, or the row was altered. Explicit deny; the caller
            // treats it as an undeliverable subscription rather than sending something unsigned.
            CryptographicOperations.ZeroMemory(plaintext);
            return false;
        }

        secret = plaintext;
        return true;
    }
}
