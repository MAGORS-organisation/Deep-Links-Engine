using System.Collections.ObjectModel;
using System.Text.Json;

using Dle.Domain.Analytics;
using Dle.Domain.Attribution;
using Dle.Domain.Contracts;
using Dle.Domain.Entities;
using Dle.Domain.Ports;
using Dle.Persistence.Repositories;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dle.Control.Features.Attribution;

/// <summary>
/// What one <c>POST /v1/events</c> batch produced.
/// </summary>
/// <param name="Accepted">Events written to the event stream.</param>
/// <param name="Rejected">Events refused: an unknown type, an unusable timestamp, or a payload
/// larger than the endpoint accepts.</param>
/// <param name="DirectOpenAttributed">Whether a <c>link_open</c> in this batch produced a new
/// <c>direct_open</c> attribution (TC-149).</param>
public readonly record struct EventBatchOutcome(int Accepted, int Rejected, bool DirectOpenAttributed);

/// <summary>
/// <c>POST /v1/events</c> — the SDK's report of what happened on the device (§B.6.4, §B.7.2,
/// FR-223, FR-226).
/// </summary>
/// <remarks>
/// <para>
/// One event type carries the weight of this endpoint. When a link is opened on a device that has
/// the application installed and the association files verify, the operating system launches the
/// application directly and <b>no HTTP request reaches the engine at all</b>. That is the normal,
/// successful case — the case the whole product exists to produce — and it is invisible to the
/// server. Without the <c>link_open</c> event the click is never counted, re-engagement campaigns
/// cannot be measured, and the statistics are understated exactly where the campaign worked best
/// (§B.6.4). Every other event type here is ordinary telemetry; this one is the difference between
/// a measurement product and a redirector.
/// </para>
/// <para>
/// A <c>link_open</c> whose URL resolves to one of the caller's own links is therefore an
/// attribution in its own right: match type <c>direct_open</c>, confidence exactly 1.00, because
/// the application is not guessing — it is reporting the URL the operating system handed it
/// (TC-149). It obeys the same rule as every other match: one installation carries at most one
/// attribution, so a direct open never displaces a decision that is already there, and only ever
/// replaces a stored <c>none</c>.
/// </para>
/// <para>
/// The batch is bounded, the events are validated one by one, and a bad event does not sink the
/// batch. An SDK that has been offline flushes a queue in one request; refusing all hundred events
/// because one carries an unknown type would lose ninety-nine good ones and give the integrator
/// nothing to debug with.
/// </para>
/// </remarks>
public sealed partial class RecordEvents
{
    /// <summary>Confidence of a direct open. It is an observation, not an estimate (TC-149).</summary>
    private const decimal DirectOpenConfidence = 1.00m;

    /// <summary>Evidence value recorded in place of a strategy name for a direct open.</summary>
    private const string DirectOpenStrategy = MatchTypeNames.DirectOpen;

    /// <summary>How far into the future a device clock may be and still be believed.</summary>
    private static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    /// <summary>How far into the past an event timestamp may reach.</summary>
    /// <remarks>
    /// A queue flushed after a long offline period is normal; a timestamp from last year is either
    /// a broken device clock or a replay, and either way it would land in a reporting bucket that
    /// has already been rolled up and published.
    /// </remarks>
    private static readonly TimeSpan PastTolerance = TimeSpan.FromDays(30);

    private readonly IAttributionStore _store;
    private readonly ISdkEventWriter _writer;
    private readonly IWebhookOutbox _outbox;
    private readonly AttributionMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly AttributionOptions _options;
    private readonly ILogger<RecordEvents> _logger;

    /// <summary>
    /// Creates the use case.
    /// </summary>
    /// <param name="store">Storage seam for installations, attributions and links.</param>
    /// <param name="writer">Batch writer of the SDK event stream.</param>
    /// <param name="outbox">Webhook outbox; a new attribution is announced.</param>
    /// <param name="metrics">Instruments (§C.6).</param>
    /// <param name="timeProvider">Clock (SHARED-KERNEL §17.2).</param>
    /// <param name="options">Attribution options.</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public RecordEvents(
        IAttributionStore store,
        ISdkEventWriter writer,
        IWebhookOutbox outbox,
        AttributionMetrics metrics,
        TimeProvider timeProvider,
        IOptions<AttributionOptions> options,
        ILogger<RecordEvents> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(outbox);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _writer = writer;
        _outbox = outbox;
        _metrics = metrics;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Records one batch.
    /// </summary>
    /// <param name="batch">The validated request body.</param>
    /// <param name="caller">The authenticated tenant and application.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many events were taken and how many were refused.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="batch"/> is <see langword="null"/>.</exception>
    public async Task<EventBatchOutcome> ExecuteAsync(
        EventBatchDto batch,
        SdkCaller caller,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        DateTimeOffset now = _timeProvider.GetUtcNow();

        Install install = await _store.GetOrCreateInstallAsync(
            new Install
            {
                TenantId = caller.TenantId,
                AppId = caller.AppId,
                InstallId = batch.InstallId,
                FirstOpenAt = now,
                Platform = NormalizePlatform(batch.Platform),
                AppVersion = Clip(batch.AppVersion, 64),

                // No referrer here. This endpoint is not the deferred path, and storing a referrer
                // the caller happened to attach would put a click identifier on the row without
                // the consent decision that /v1/resolve makes before it does the same thing.
                RawReferrer = null,
                CreatedAt = now,
            },
            cancellationToken);

        List<SdkEvent> accepted = new(batch.Events.Count);
        ReportedLink? openedLink = null;
        long? openedLinkId = null;
        int rejected = 0;

        foreach (EventDto candidate in batch.Events)
        {
            if (!SdkEventNames.TryParse(candidate.Type, out SdkEventType type))
            {
                // Fail closed: an unrecognised type is counted, never quietly filed as "custom".
                rejected++;
                continue;
            }

            if (!TryReadTimestamp(candidate.Ts, now, out DateTimeOffset occurredAt))
            {
                rejected++;
                continue;
            }

            long? linkId = null;
            ReportedLink? eventLink = null;

            if (type == SdkEventType.LinkOpen && LinkUrlParser.TryParse(candidate.Url, out ReportedLink reported))
            {
                AttributedLink? link = await _store.FindLinkByHostAndSlugAsync(
                    reported.Host,
                    reported.Slug,
                    cancellationToken);

                // A link of another tenant is treated exactly like a URL that is not ours at all.
                // Anything else would let one tenant confirm the existence of another's slug
                // (SHARED-KERNEL §17.7, TC-166).
                if (link is not null && link.TenantId == caller.TenantId)
                {
                    linkId = link.Id;
                    eventLink = reported;

                    // The first match in the batch is the one the attribution is made from: a batch
                    // flushed after an offline period is ordered oldest first, so the first link
                    // open is the one that actually brought the user in.
                    openedLink ??= reported;
                    openedLinkId ??= link.Id;
                }
            }

            accepted.Add(new SdkEvent
            {
                Id = Guid.CreateVersion7(occurredAt),
                OccurredAt = occurredAt,
                TenantId = caller.TenantId,
                AppId = caller.AppId,
                InstallId = batch.InstallId,
                Type = type,
                Name = Clip(candidate.Name, 128),

                // The URL is kept only when it resolved to one of our links, and then only as the
                // canonical short form. A URL that is not ours is not our data to store (§E.6.3).
                Url = CanonicalUrl(eventLink),
                Value = candidate.Value,
                Currency = NormalizeCurrency(candidate.Currency),
                LinkId = linkId,
                ClickId = null,
                Properties = candidate.Properties,
            });
        }

        if (accepted.Count > 0)
        {
            await _writer.WriteBatchAsync(accepted, cancellationToken);
        }

        bool attributed = false;

        if (openedLinkId is { } matchedLinkId && openedLink is { } matchedUrl)
        {
            attributed = await AttributeDirectOpenAsync(
                install,
                caller,
                matchedLinkId,
                matchedUrl,
                now,
                cancellationToken);
        }

        return new EventBatchOutcome(accepted.Count, rejected, attributed);
    }

    /// <summary>
    /// Records the attribution a direct open earns (TC-149, FR-223).
    /// </summary>
    /// <param name="install">The installation row.</param>
    /// <param name="caller">The authenticated tenant and application.</param>
    /// <param name="linkId">The link the reported URL resolved to.</param>
    /// <param name="reported">The reported URL, already reduced to host and slug.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when this call created the attribution.</returns>
    /// <remarks>
    /// <para>
    /// No consent gate stands in front of this, and the reason is worth stating rather than
    /// assuming. The deferred strategies join an anonymous web click to an installation across two
    /// sessions and two contexts, which is what §E.6.2 calls <c>full</c> mode and what ePrivacy
    /// art. 5(3) puts behind documented consent. A direct open joins nothing: the operating system
    /// handed the application a URL, the application reports the URL it was given, and the engine
    /// looks up which of the tenant's own links it is. No click is read, no device signal is
    /// touched, no identifier crosses a context — the consent decision is still recorded in the
    /// evidence so the reasoning is auditable, but it does not gate the record.
    /// </para>
    /// <para>
    /// The two uniqueness rules of §B.5.3 still hold. An installation that already carries a real
    /// match keeps it, because a deferred deterministic match explains where the install came from
    /// and a later direct open only says the application was opened again. A stored <c>none</c> is
    /// replaced, because a direct open is deterministic evidence the earlier decision did not
    /// have. <c>click_id</c> stays null: there was no click.
    /// </para>
    /// </remarks>
    private async Task<bool> AttributeDirectOpenAsync(
        Install install,
        SdkCaller caller,
        long linkId,
        ReportedLink reported,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        AttributionRecord? stored = await _store.FindAttributionByInstallAsync(install.Id, cancellationToken);

        if (stored is not null && !string.Equals(stored.MatchType, MatchTypeNames.None, StringComparison.Ordinal))
        {
            return false;
        }

        AttributionEvidence evidence = new AttributionEvidence()
            .With(AttributionEvidence.StrategyKey, DirectOpenStrategy)
            .With(AttributionEvidence.OpenedUrlKey, CanonicalUrl(reported))
            .With(AttributionEvidence.ConsentKey, "direct_open_first_party");

        AttributionRecord record = new()
        {
            TenantId = caller.TenantId,
            InstallId = install.Id,
            ClickId = null,
            LinkId = linkId,
            MatchType = MatchTypeNames.DirectOpen,
            Confidence = DirectOpenConfidence,
            MatchedAt = now,
            WindowSeconds = null,
            Evidence = AttributionEvidence.ToJson(evidence.ToDictionary()),
        };

        AttributionWriteResult write = stored is null
            ? await _store.TryCreateAttributionAsync(record, cancellationToken)
            : await _store.TryUpgradeNoneAttributionAsync(record, cancellationToken);

        if (write.Outcome != AttributionOutcome.Created || write.Record is null)
        {
            // Another call won the race, or the store refused. Either way the installation already
            // carries the decision that stands, and this call adds nothing (SHARED-KERNEL §17.9).
            return false;
        }

        _metrics.Decision(MatchTypeNames.DirectOpen, DirectOpenConfidence);

        await AnnounceAsync(write.Record, install, caller, cancellationToken);

        LogDirectOpen(_logger, caller.TenantId, linkId);

        return true;
    }

    /// <summary>Puts <c>attribution.created</c> on the webhook outbox (§B.7.4).</summary>
    private async Task AnnounceAsync(
        AttributionRecord record,
        Install install,
        SdkCaller caller,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> data = new(StringComparer.Ordinal)
        {
            ["install_id"] = install.InstallId,
            ["app_id"] = caller.AppId.ToString(),
            ["match_type"] = record.MatchType,
            ["confidence"] = record.Confidence.ToString("0.00", CultureInfo.InvariantCulture),
        };

        if (record.LinkId is { } linkId)
        {
            data["link_id"] = linkId.ToString(CultureInfo.InvariantCulture);
        }

        WebhookPayload payload = new()
        {
            Event = WebhookEvents.AttributionCreated,
            Id = record.Id.ToString(),
            OccurredAt = record.MatchedAt,
            Data = new ReadOnlyDictionary<string, string>(data),
        };

        await _outbox.EnqueueAsync(
            caller.TenantId,
            WebhookEvents.AttributionCreated,
            JsonSerializer.Serialize(payload, AttributionJsonContext.Default.WebhookPayload),
            cancellationToken);
    }

    /// <summary>
    /// Decides which instant an event happened at.
    /// </summary>
    /// <param name="reported">The instant the device reported, or <see langword="null"/>.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="occurredAt">The instant to store.</param>
    /// <returns><see langword="false"/> when the reported instant is unusable.</returns>
    /// <remarks>
    /// An absent timestamp means "on arrival", which is the documented contract. A timestamp
    /// outside the tolerances is refused rather than clamped: clamping would move a device's clock
    /// error into a reporting bucket silently, and the integrator would never learn that the clock
    /// is wrong.
    /// </remarks>
    private static bool TryReadTimestamp(DateTimeOffset? reported, DateTimeOffset now, out DateTimeOffset occurredAt)
    {
        if (reported is not { } value)
        {
            occurredAt = now;
            return true;
        }

        occurredAt = value.ToUniversalTime();

        return occurredAt <= now + FutureTolerance && occurredAt >= now - PastTolerance;
    }

    /// <summary>Renders the canonical short URL of a reported link, without query or fragment.</summary>
    private static string? CanonicalUrl(ReportedLink? reported) => reported is { } value
        ? string.Create(CultureInfo.InvariantCulture, $"https://{value.Host}/{value.Slug}")
        : null;

    /// <summary>Stored spelling of the platform an event batch was reported from.</summary>
    private static string NormalizePlatform(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "ios" => "ios",
            "android" => "android",
            "desktop" => "desktop",
            "other" => "other",
            _ => "unknown",
        };
    }

    /// <summary>Normalizes an ISO 4217 code, dropping anything that is not one.</summary>
    private static string? NormalizeCurrency(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();

        if (trimmed.Length != 3)
        {
            return null;
        }

        foreach (char character in trimmed)
        {
            if (!char.IsAsciiLetter(character))
            {
                return null;
            }
        }

        return trimmed.ToUpperInvariant();
    }

    private static string? Clip(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();

        return trimmed.Length <= maximum ? trimmed : trimmed[..maximum];
    }

    /// <summary>Largest event batch this use case will look at, from configuration.</summary>
    /// <remarks>
    /// Exposed so the endpoint can refuse an oversized batch before deserializing it fully, which
    /// is the cheaper place to say no.
    /// </remarks>
    public int MaxRequestBytes => _options.MaxRequestBytes;

    [LoggerMessage(
        EventId = 5401,
        Level = LogLevel.Debug,
        Message = "A direct open reported by tenant {TenantId} was attributed to link {LinkId} with confidence 1.00.")]
    private static partial void LogDirectOpen(ILogger logger, Guid tenantId, long linkId);
}
