using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

using Dle.Control.Features.Jwks;
using Dle.Crypto;
using Dle.Domain.Crypto;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dle.Control.Features.Webhooks;

/// <summary>
/// The key material behind the asymmetric slot of a webhook signature, and the source that
/// publishes it at <c>/.well-known/jwks.json</c> (§B.7.4, §E.4.1 K4, S-12).
/// </summary>
/// <remarks>
/// <para>
/// Webhook keys are bound to the <see cref="SigningKeyPurposes.Webhook"/> purpose and are not the
/// token keys. That separation is §B.5.2's, and it earns its keep in exactly one scenario: a key
/// that leaks from the signing path of an outbound HTTP client must not also be a key that mints
/// click tokens the edge will accept.
/// </para>
/// <para>
/// Two sources, merged, never exclusive. A deployment that manages key material — through
/// configuration or through a database backed <see cref="ISigningKeyStore"/> — gets exactly those
/// keys. A deployment that has configured nothing but the master secret still gets a working
/// asymmetric slot, because one is derived from that secret with HKDF under a label of its own.
/// Derivation rather than generation is what makes the answer identical on every replica and
/// across restarts: two control plane instances sign under the same key, and a receiver that
/// fetched the key set yesterday can still verify what was signed today.
/// </para>
/// <para>
/// Every key inside its validity window is published, retired ones included. That is what makes
/// rotation invisible to a receiver: the new key appears before it signs, the old one stays until
/// nothing signed under it can still be in flight, and a verifier that refreshes on an unrecognised
/// <c>kid</c> never sees a gap (S-12).
/// </para>
/// </remarks>
public sealed partial class WebhookSigningKeys : IJwksContributor, IDisposable
{
    /// <summary>HKDF label of the derived webhook signing key. Part of the on-disk contract.</summary>
    private const string KeyLabel = "dle:webhook:ed25519:v1";

    /// <summary>HKDF label of the identifier of the derived key.</summary>
    private const string KeyIdLabel = "dle:webhook:kid:v1";

    /// <summary>Prefix of a derived key identifier, so it is recognisable in a header.</summary>
    private const string KeyIdPrefix = "whk-";

    /// <summary>Bytes of entropy behind a derived key identifier.</summary>
    private const int KeyIdEntropyBytes = 9;

    /// <summary>How often the durable store is consulted again.</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(10);

    private readonly ISigningKeyStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WebhookSigningKeys> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SigningKeyMaterial _derived;

    private WebhookKeySet _keys;
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private bool _disposed;

    /// <summary>
    /// Creates the key source and derives the bootstrap key.
    /// </summary>
    /// <param name="store">Durable home of the key material.</param>
    /// <param name="crypto">Crypto options, read for the master secret.</param>
    /// <param name="timeProvider">Clock (SHARED-KERNEL §17.2).</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// The bootstrap key is derived in the constructor, synchronously and without I/O, so the JWKS
    /// endpoint and the dispatcher both have something to work with before the first database call
    /// has happened. The durable store is consulted on first use and then periodically.
    /// </remarks>
    public WebhookSigningKeys(
        ISigningKeyStore store,
        IOptions<CryptoOptions> crypto,
        TimeProvider timeProvider,
        ILogger<WebhookSigningKeys> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(crypto);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _timeProvider = timeProvider;
        _logger = logger;
        _derived = Derive(crypto.Value.MasterSecret);
        _keys = WebhookKeySet.From([_derived], _timeProvider.GetUtcNow());
    }

    /// <summary>
    /// The signer of the asymmetric slot, refreshing the key set when it is stale.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The current signer.</returns>
    public async ValueTask<ISigner> GetSignerAsync(CancellationToken cancellationToken)
    {
        WebhookKeySet keys = await EnsureLoadedAsync(cancellationToken);

        return keys.Signer;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Synchronous by contract, so it answers from the last loaded snapshot. Before the first load
    /// that snapshot holds the derived key, which is the key that would actually be signing at that
    /// moment — the endpoint never publishes a set that does not include the key in use.
    /// </remarks>
    public IReadOnlyList<JsonWebKey> GetKeys(DateTimeOffset asOf)
    {
        WebhookKeySet keys = Volatile.Read(ref _keys);
        List<JsonWebKey> published = [];

        foreach (JsonWebKey key in keys.PublicKeys)
        {
            if (key.NotAfter is null || key.NotAfter.Value >= asOf)
            {
                published.Add(key);
            }
        }

        return published;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    /// <summary>Loads the durable key material when the snapshot is older than the refresh interval.</summary>
    private async ValueTask<WebhookKeySet> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (now - _loadedAt < RefreshInterval)
        {
            return Volatile.Read(ref _keys);
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (now - _loadedAt < RefreshInterval)
            {
                return Volatile.Read(ref _keys);
            }

            IReadOnlyList<SigningKeyMaterial> stored =
                await _store.LoadAsync(SigningKeyPurposes.Webhook, cancellationToken);

            List<SigningKeyMaterial> material = [.. stored];

            if (!material.Exists(candidate => candidate.PrivateKey.Length > 0 && candidate.IsValidAt(now)))
            {
                // Nothing durable can sign right now, so the derived key stays in the set. It is
                // added rather than substituted, so a store that holds retired keys keeps
                // publishing them and a receiver can still verify an older delivery (S-12).
                material.Add(_derived);
                LogUsingDerivedKey(_logger, _derived.Kid);
            }

            WebhookKeySet refreshed = WebhookKeySet.From(material, now);

            Volatile.Write(ref _keys, refreshed);
            _loadedAt = now;

            return refreshed;
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    /// <summary>
    /// Derives the bootstrap Ed25519 key from the master secret.
    /// </summary>
    /// <param name="masterSecret">The configured master secret.</param>
    /// <returns>The derived material, public half included.</returns>
    /// <remarks>
    /// HKDF-SHA-256 under a webhook specific label, so this key and the token key are independent:
    /// recovering one tells an attacker nothing about the other, which is the property the purpose
    /// separation in §B.5.2 is trying to buy.
    /// </remarks>
    private static SigningKeyMaterial Derive(string masterSecret)
    {
        byte[] ikm = Encoding.UTF8.GetBytes(masterSecret);

        try
        {
            byte[] privateKey = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                ikm,
                32,
                Salt,
                Encoding.UTF8.GetBytes(KeyLabel));

            byte[] identifier = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                ikm,
                KeyIdEntropyBytes,
                Salt,
                Encoding.UTF8.GetBytes(KeyIdLabel));

            // FromOptions derives the public half, so the parsing of Ed25519 material happens in
            // exactly one place in the solution rather than being repeated here.
            return SigningKeyFactory.FromOptions(new SigningKeyOptions
            {
                Kid = KeyIdPrefix + Base64Url.EncodeToString(identifier),
                Algorithm = SignatureAlgorithms.Ed25519,
                PrivateKey = Convert.ToBase64String(privateKey),
                Purpose = SigningKeyPurposes.Webhook,
                IsCurrent = true,
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ikm);
        }
    }

    /// <summary>Fixed HKDF salt. A constant salt is sound; the entropy is in the secret.</summary>
    private static readonly byte[] Salt = "dle:webhook:hkdf:v1"u8.ToArray();

    [LoggerMessage(
        EventId = 5501,
        Level = LogLevel.Debug,
        Message = "No durable webhook signing key is available; deliveries are signed with the derived key {Kid}.")]
    private static partial void LogUsingDerivedKey(ILogger logger, string kid);
}

/// <summary>
/// A resolved set of webhook keys: one that signs, and every one that is published.
/// </summary>
/// <param name="Signer">The signer of the asymmetric slot.</param>
/// <param name="PublicKeys">Every key to publish, retired ones included.</param>
public sealed record WebhookKeySet(ISigner Signer, IReadOnlyList<JsonWebKey> PublicKeys)
{
    /// <summary>
    /// Builds the set from key material.
    /// </summary>
    /// <param name="material">The candidate keys.</param>
    /// <param name="now">The current instant.</param>
    /// <returns>The set.</returns>
    /// <exception cref="InvalidOperationException">No candidate can sign at
    /// <paramref name="now"/>.</exception>
    /// <remarks>
    /// The signer is the current key when one is marked current and valid, otherwise the first key
    /// that holds a private half and is inside its window. A set with nothing to sign with is an
    /// error rather than a silent "sign only with v1": the asymmetric slot is the one a third party
    /// verifies, and quietly dropping it would turn a key management failure into an integration
    /// that looks fine until somebody audits it.
    /// </remarks>
    public static WebhookKeySet From(IReadOnlyList<SigningKeyMaterial> material, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(material);

        SigningKeyMaterial? current = null;
        List<JsonWebKey> published = [];

        foreach (SigningKeyMaterial candidate in material)
        {
            if (SigningKeyFactory.CreateVerificationKey(candidate).ToJsonWebKey() is { } jwk)
            {
                published.Add(jwk);
            }

            if (candidate.PrivateKey.Length == 0 || !candidate.IsValidAt(now))
            {
                continue;
            }

            if (current is null || (candidate.IsCurrent && !current.IsCurrent))
            {
                current = candidate;
            }
        }

        if (current is null)
        {
            throw new InvalidOperationException(
                "No webhook signing key is valid at this instant. Configure a key with purpose "
                + "'webhook' under Dle:Crypto:Keys, or leave the set empty so the engine derives "
                + "one from Dle:Crypto:MasterSecret.");
        }

        return new WebhookKeySet(SigningKeyFactory.CreateSigner(current), published);
    }
}
