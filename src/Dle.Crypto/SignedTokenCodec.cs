using System.Buffers;
using System.Buffers.Text;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Options;

namespace Dle.Crypto;

/// <summary>
/// Writes and reads tokens in the engine format
/// <c>dlt1.&lt;alg&gt;.&lt;kid&gt;.&lt;base64url(payload)&gt;.&lt;base64url(signature)&gt;</c> (§E.4.2).
/// </summary>
/// <remarks>
/// <para>
/// The signature covers the first four segments, not just the payload. Signing the payload alone
/// would leave <c>alg</c> and <c>kid</c> unauthenticated, and an attacker could rewrite them to
/// point at a weaker algorithm or a different key and have the token still verify. This is the
/// classic JWT algorithm confusion failure, and binding the header into the signed input is what
/// removes it.
/// </para>
/// <para>
/// Validation is split in two. <see cref="Validate"/> checks the signature, the audience and the
/// clock, and is what a stateless receiver needs. <see cref="ValidateOnceAsync"/> additionally
/// burns the <c>jti</c> through an <see cref="ITokenReplayGuard"/>, and is what a single use token
/// such as a claim ticket needs. Keeping them separate means the cheap check does not pay for a
/// round trip it does not need.
/// </para>
/// </remarks>
public sealed class SignedTokenCodec
{
    /// <summary>Bytes of randomness behind a token identifier.</summary>
    private const int JtiEntropyBytes = 16;

    private readonly IKeyRing _keyRing;
    private readonly ITokenReplayGuard _replayGuard;
    private readonly TimeProvider _timeProvider;
    private readonly CryptoOptions _options;

    /// <summary>
    /// Creates a codec.
    /// </summary>
    /// <param name="keyRing">Source of the signer and the verifier.</param>
    /// <param name="replayGuard">Guard used by <see cref="ValidateOnceAsync"/>.</param>
    /// <param name="options">The crypto options.</param>
    /// <param name="timeProvider">Clock. SHARED-KERNEL §17.2 rules out reading the machine clock.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public SignedTokenCodec(
        IKeyRing keyRing,
        ITokenReplayGuard replayGuard,
        IOptions<CryptoOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(keyRing);
        ArgumentNullException.ThrowIfNull(replayGuard);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _keyRing = keyRing;
        _replayGuard = replayGuard;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Issues a token with the current signing key.
    /// </summary>
    /// <param name="audience">Value of the <c>aud</c> claim, or <see langword="null"/> to use
    /// <see cref="CryptoOptions.TokenAudience"/>.</param>
    /// <param name="claims">Application specific claims, or <see langword="null"/>.</param>
    /// <param name="lifetime">Validity, or <see langword="null"/> to use
    /// <see cref="CryptoOptions.TokenLifetimeSeconds"/>.</param>
    /// <returns>The token in its wire format.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lifetime"/> is not positive.</exception>
    public string Issue(
        string? audience = null,
        IReadOnlyDictionary<string, string>? claims = null,
        TimeSpan? lifetime = null)
    {
        TimeSpan effectiveLifetime = lifetime ?? TimeSpan.FromSeconds(_options.TokenLifetimeSeconds);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(effectiveLifetime, TimeSpan.Zero, nameof(lifetime));

        DateTimeOffset now = _timeProvider.GetUtcNow();

        SignedTokenPayload payload = new()
        {
            Iat = now.ToUnixTimeSeconds(),
            Exp = (now + effectiveLifetime).ToUnixTimeSeconds(),
            Jti = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(JtiEntropyBytes)),
            Aud = string.IsNullOrEmpty(audience) ? _options.TokenAudience : audience,
            Claims = claims,
        };

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, DleCryptoJsonContext.Default.SignedTokenPayload);
        string encodedPayload = Base64Url.EncodeToString(json);

        ISigner signer = _keyRing.CurrentSigner;
        string signingInput = BuildSigningInput(signer.AlgorithmId, signer.KeyId, encodedPayload);
        byte[] signature = signer.Sign(Encoding.UTF8.GetBytes(signingInput));

        return new SignedToken(
            signer.AlgorithmId,
            signer.KeyId,
            encodedPayload,
            Base64Url.EncodeToString(signature)).ToString();
    }

    /// <summary>
    /// Verifies a token and checks its audience and clock claims.
    /// </summary>
    /// <param name="token">The token as received. Untrusted.</param>
    /// <param name="expectedAudience">Audience the caller expects, or <see langword="null"/> to
    /// use <see cref="CryptoOptions.TokenAudience"/>.</param>
    /// <returns>The outcome. This method never throws: it runs on untrusted input.</returns>
    public TokenValidationResult Validate(string? token, string? expectedAudience = null)
    {
        if (!SignedToken.TryParse(token, out SignedToken parsed))
        {
            return TokenValidationResult.Failure(TokenValidationResult.ReasonMalformed);
        }

        if (!TryDecodeBase64Url(parsed.Payload, out byte[] payloadBytes) ||
            !TryDecodeBase64Url(parsed.Signature, out byte[] signatureBytes))
        {
            return TokenValidationResult.Failure(TokenValidationResult.ReasonMalformed);
        }

        byte[] signingInput = Encoding.UTF8.GetBytes(
            BuildSigningInput(parsed.Alg, parsed.Kid, parsed.Payload));

        if (!_keyRing.Verifier.Verify(parsed.Alg, parsed.Kid, signingInput, signatureBytes))
        {
            return TokenValidationResult.Failure(TokenValidationResult.ReasonSignature);
        }

        SignedTokenPayload? payload = DeserializePayload(payloadBytes);

        if (payload is null)
        {
            return TokenValidationResult.Failure(TokenValidationResult.ReasonMalformedPayload);
        }

        string audience = string.IsNullOrEmpty(expectedAudience) ? _options.TokenAudience : expectedAudience;

        if (!string.Equals(payload.Aud, audience, StringComparison.Ordinal))
        {
            return TokenValidationResult.Failure(TokenValidationResult.ReasonAudience);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        TimeSpan skew = TimeSpan.FromSeconds(_options.TokenClockSkewSeconds);

        if (payload.ExpiresAt + skew < now)
        {
            return TokenValidationResult.Failure(TokenValidationResult.ReasonExpired);
        }

        if (payload.IssuedAt - skew > now)
        {
            return TokenValidationResult.Failure(TokenValidationResult.ReasonNotYetValid);
        }

        return TokenValidationResult.Success(payload);
    }

    /// <summary>
    /// Verifies a token and consumes its identifier, so that presenting it twice fails.
    /// </summary>
    /// <param name="token">The token as received. Untrusted.</param>
    /// <param name="expectedAudience">Audience the caller expects, or <see langword="null"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    public async ValueTask<TokenValidationResult> ValidateOnceAsync(
        string? token,
        string? expectedAudience,
        CancellationToken ct)
    {
        TokenValidationResult result = Validate(token, expectedAudience);

        if (!result.IsValid || result.Payload is null)
        {
            return result;
        }

        bool consumed = await _replayGuard.TryConsumeAsync(result.Payload.Jti, result.Payload.ExpiresAt, ct);

        return consumed ? result : TokenValidationResult.Failure(TokenValidationResult.ReasonReplayed);
    }

    /// <summary>
    /// Builds the bytes that are signed: everything up to and including the payload segment, so
    /// that the algorithm and the key identifier are authenticated along with the payload.
    /// </summary>
    private static string BuildSigningInput(string algorithmId, string keyId, string encodedPayload) =>
        string.Join('.', SignedToken.Prefix, algorithmId, keyId, encodedPayload);

    private static bool TryDecodeBase64Url(string value, out byte[] decoded)
    {
        byte[] buffer = new byte[Base64Url.GetMaxDecodedLength(value.Length)];

        if (Base64Url.DecodeFromChars(value, buffer, out _, out int written) != OperationStatus.Done)
        {
            decoded = [];
            return false;
        }

        decoded = buffer[..written];
        return true;
    }

    /// <summary>
    /// Deserializes the payload. A payload that is not the JSON this module writes is a validation
    /// failure, not an exception: the input is untrusted and reached this point having already
    /// passed the signature check only in the sense that the bytes were signed by us.
    /// </summary>
    private static SignedTokenPayload? DeserializePayload(byte[] payloadBytes)
    {
        try
        {
            return JsonSerializer.Deserialize(payloadBytes, DleCryptoJsonContext.Default.SignedTokenPayload);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
