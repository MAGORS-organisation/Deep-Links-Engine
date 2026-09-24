using Dle.Crypto;
using Dle.Domain.Attribution;
using Dle.Domain.Entities;

using Microsoft.Extensions.Options;

namespace Dle.Control.Features.Attribution;

/// <summary>Why a presented claim code could or could not be redeemed (TC-148).</summary>
public enum ClaimCodeStatus
{
    /// <summary>The code is valid, unexpired and unspent.</summary>
    Valid = 0,

    /// <summary>The strategy is switched off for this deployment.</summary>
    Disabled = 1,

    /// <summary>The text is not six characters of the claim code alphabet.</summary>
    Malformed = 2,

    /// <summary>No such code was issued for this tenant.</summary>
    Unknown = 3,

    /// <summary>The code existed but its time to live has passed.</summary>
    Expired = 4,

    /// <summary>The code has already been redeemed once.</summary>
    Consumed = 5,
}

/// <summary>The outcome of redeeming a claim code.</summary>
/// <param name="Status">Whether it may be used, and if not, why.</param>
/// <param name="Record">The code row when <paramref name="Status"/> is
/// <see cref="ClaimCodeStatus.Valid"/>, otherwise <see langword="null"/>.</param>
public sealed record ClaimCodeRedemption(ClaimCodeStatus Status, ClaimCodeRecord? Record);

/// <summary>
/// Issues and redeems the six characters shown on the interstitial page (S3, FR-184, §E.4.1 K3,
/// T-17).
/// </summary>
/// <remarks>
/// <para>
/// A claim code carries about 28 bits. It is not a credential and this class does not pretend
/// otherwise: what makes it safe is the combination of a short time to live, single use, a keyed
/// hash in storage, a constant time comparison and a rate limit on both issuance and redemption.
/// Remove any one of those and the remaining ones stop being enough.
/// </para>
/// <para>
/// Only the hash is stored. A plain digest of a 28 bit value falls to a laptop in seconds, so the
/// hash is keyed with a pepper that lives in the process and never in the database — an attacker
/// needs both halves to get anywhere.
/// </para>
/// </remarks>
public sealed class ClaimCodeService
{
    private readonly IAttributionStore _store;
    private readonly ClaimCodeGenerator _generator;
    private readonly TimeProvider _timeProvider;
    private readonly AttributionMetrics _metrics;
    private readonly ClaimCodeOptions _options;

    /// <summary>
    /// Creates the service.
    /// </summary>
    /// <param name="store">Storage seam.</param>
    /// <param name="generator">Code generator and verifier from the crypto module.</param>
    /// <param name="timeProvider">Clock. Never <c>DateTime.UtcNow</c> (SHARED-KERNEL §17.2).</param>
    /// <param name="metrics">Instruments.</param>
    /// <param name="options">Attribution options.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public ClaimCodeService(
        IAttributionStore store,
        ClaimCodeGenerator generator,
        TimeProvider timeProvider,
        AttributionMetrics metrics,
        IOptions<AttributionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(options);

        _store = store;
        _generator = generator;
        _timeProvider = timeProvider;
        _metrics = metrics;
        _options = options.Value.ClaimCode;
    }

    /// <summary>Whether codes may be issued and redeemed at all.</summary>
    public bool Enabled => _options.Enabled;

    /// <summary>
    /// Issues a code for a click.
    /// </summary>
    /// <param name="tenantId">Owning tenant.</param>
    /// <param name="clickId">The click the code stands for.</param>
    /// <param name="linkId">The link the click belongs to, when known.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The code and its expiry. The plaintext exists only in the returned object.</returns>
    /// <exception cref="InvalidOperationException">The strategy is disabled.</exception>
    public async Task<ClaimCodeResponse> IssueAsync(
        Guid tenantId,
        string clickId,
        long? linkId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clickId);

        if (!_options.Enabled)
        {
            throw new InvalidOperationException(
                "Dle:Attribution:ClaimCode:Enabled is false, so no code can be issued.");
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset expiresAt = now + _options.Ttl;
        ClaimCodeCredential credential = _generator.Create();

        _ = await _store.AddClaimCodeAsync(
            new ClaimCodeRecord
            {
                TenantId = tenantId,
                CodeHash = credential.Hash,
                ClickId = clickId,
                LinkId = linkId,
                ExpiresAt = expiresAt,
                CreatedAt = now,
            },
            cancellationToken);

        _metrics.ClaimCode("issued");

        return new ClaimCodeResponse
        {
            Code = credential.Code,
            ExpiresAt = expiresAt,
            ExpiresIn = (int)Math.Max(Math.Ceiling((expiresAt - now).TotalSeconds), 0),
        };
    }

    /// <summary>
    /// Looks a presented code up and decides whether it may be redeemed.
    /// </summary>
    /// <param name="presentedCode">The code as typed by the user. Untrusted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome. Consuming the code is a separate step, so the caller only spends it
    /// once it knows what the attribution write did.</returns>
    /// <remarks>
    /// The lookup is by keyed hash, and the deciding comparison is still
    /// <see cref="ClaimCodeGenerator.Verify"/>, which is constant time. The database equality that
    /// found the row compares 32 bytes of HMAC output, from which no timing signal about the code
    /// itself can be extracted; the in-process comparison is what the security property rests on
    /// (T-17, SHARED-KERNEL §17.6).
    /// </remarks>
    public async Task<ClaimCodeRedemption> InspectAsync(string? presentedCode, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return new ClaimCodeRedemption(ClaimCodeStatus.Disabled, Record: null);
        }

        if (string.IsNullOrWhiteSpace(presentedCode))
        {
            return new ClaimCodeRedemption(ClaimCodeStatus.Malformed, Record: null);
        }

        string normalized = ClaimCode.Normalize(presentedCode);

        if (!ClaimCode.IsWellFormed(normalized))
        {
            _metrics.ClaimCode("malformed");
            return new ClaimCodeRedemption(ClaimCodeStatus.Malformed, Record: null);
        }

        byte[] hash = _generator.HashOf(normalized);
        ClaimCodeRecord? record = await _store.FindClaimCodeAsync(hash, cancellationToken);

        if (record is null || !_generator.Verify(normalized, record.CodeHash))
        {
            _metrics.ClaimCode("unknown");
            return new ClaimCodeRedemption(ClaimCodeStatus.Unknown, Record: null);
        }

        if (record.ConsumedAt is not null)
        {
            _metrics.ClaimCode("consumed");
            return new ClaimCodeRedemption(ClaimCodeStatus.Consumed, Record: null);
        }

        if (record.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            _metrics.ClaimCode("expired");
            return new ClaimCodeRedemption(ClaimCodeStatus.Expired, Record: null);
        }

        return new ClaimCodeRedemption(ClaimCodeStatus.Valid, record);
    }

    /// <summary>Spends a code so it can never be redeemed again.</summary>
    /// <param name="record">The code row returned by <see cref="InspectAsync"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the code is spent.</returns>
    /// <remarks>
    /// The code is spent whether or not the attribution it pointed at could be written. If another
    /// installation already claimed that click, the code has nothing left to buy, and leaving it
    /// outstanding would only widen the window in which it can be guessed.
    /// </remarks>
    public async Task ConsumeAsync(ClaimCodeRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        await _store.ConsumeClaimCodeAsync(record.Id, _timeProvider.GetUtcNow(), cancellationToken);
        _metrics.ClaimCode("redeemed");
    }
}
