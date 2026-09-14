using System.Collections.ObjectModel;

namespace Dle.Domain.Contracts;

/// <summary>
/// Body of <c>POST /v1/resolve</c>, the call an SDK makes once on the first launch after install
/// to recover the context of the click that led to it (§B.7.2, UC-04, UC-05).
/// </summary>
public sealed record ResolveRequestDto
{
    /// <summary>Identifier the SDK generated and persisted for this installation. Deduplication and
    /// replay protection key off it (FR-188, TC-143).</summary>
    public required string InstallId { get; init; }

    /// <summary>Client platform: <c>ios</c>, <c>android</c>, <c>desktop</c> or <c>other</c>.</summary>
    public required string Platform { get; init; }

    /// <summary>Host application version, for version scoped routing rules (FR-124).</summary>
    public string? AppVersion { get; init; }

    /// <summary>Operating system version reported by the device.</summary>
    public string? OsVersion { get; init; }

    /// <summary>Raw Google Play Install Referrer string. The deterministic Android path (S1, FR-182).</summary>
    public string? Referrer { get; init; }

    /// <summary>Code the user typed from the interstitial page. A deterministic iOS path (S3, FR-184).</summary>
    public string? ClaimCode { get; init; }

    /// <summary>Opaque, already hashed account identifier used to reconcile a web session with a
    /// signed in user. A deterministic path on both platforms (S2, FR-185).</summary>
    public string? LoginKey { get; init; }

    /// <summary>Device signals for probabilistic matching. Sent only when the module is enabled and
    /// attribution consent was given; otherwise the SDK omits the object entirely (TC-145, TC-146).</summary>
    public DeviceSignalsDto? Signals { get; init; }

    /// <summary>Recorded consent. Under a tenant in <c>full</c> mode, click-id linking of any kind -
    /// the deterministic install referrer included - happens only when <c>attribution</c> is true;
    /// absent or negative consent answers <c>match_type=none</c> with reason <c>consent_missing</c>
    /// and a non-zero <c>expires_in</c>, so the SDK asks again once consent is recorded
    /// (ePrivacy art. 5(3), §E.6.2). Device signals are stored only with it too.</summary>
    public ConsentDto? Consent { get; init; }
}

/// <summary>
/// Device signals offered for probabilistic matching. Deliberately coarse: nothing here is a
/// stable device identifier, and the server drops the whole object when consent is missing.
/// </summary>
public sealed record DeviceSignalsDto
{
    /// <summary>BCP 47 language tag reported by the device, for example <c>sk-SK</c>.</summary>
    public string? Language { get; init; }

    /// <summary>Screen size as width by height in physical pixels, for example <c>1080x2400</c>.</summary>
    public string? Screen { get; init; }

    /// <summary>UTC offset in minutes at the time of the call.</summary>
    public int? TzOffset { get; init; }

    /// <summary>Coarse device model string.</summary>
    public string? DeviceModel { get; init; }
}

/// <summary>Consent as recorded by the host application or a consent management platform.</summary>
public sealed record ConsentDto
{
    /// <summary>Consent to analytics processing.</summary>
    public bool Analytics { get; init; }

    /// <summary>Consent to attribution processing. Without it no cross session linking happens.</summary>
    public bool Attribution { get; init; }

    /// <summary>When consent was captured. Retained so the decision stays auditable.</summary>
    public DateTimeOffset? Ts { get; init; }
}

/// <summary>
/// Response to <c>POST /v1/resolve</c>. Always states how the match was made and how much to trust
/// it: a probabilistic result is never dressed up as a certainty (FR-186, ADR-008).
/// </summary>
public sealed record ResolveResponseDto
{
    /// <summary>Whether any strategy matched. When false the app continues its normal onboarding.</summary>
    public required bool Matched { get; init; }

    /// <summary>Strategy that produced the match: <c>none</c>, <c>install_referrer</c>, <c>login</c>,
    /// <c>claim_code</c>, <c>probabilistic</c> or <c>direct_open</c>.</summary>
    public required string MatchType { get; init; }

    /// <summary>Confidence from 0.00 to 1.00. Exactly 1.00 for deterministic strategies.</summary>
    public required decimal Confidence { get; init; }

    /// <summary>Identifier of the matched click.</summary>
    public string? ClickId { get; init; }

    /// <summary>The matched link, when there is one.</summary>
    public ResolveLinkDto? Link { get; init; }

    /// <summary>Parameters handed to the application, typically the UTM set of the link plus custom data.</summary>
    public IReadOnlyDictionary<string, string> Params { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Seconds this result stays valid. Zero means it is final and must not be re-requested;
    /// a no-match answer given for want of consent carries the remaining install-referrer window
    /// instead, and an SDK re-asks after it, or sooner once attribution consent is recorded.</summary>
    public int ExpiresIn { get; init; }
}

/// <summary>The link an install was attributed to, reduced to what the client needs to navigate.</summary>
public sealed record ResolveLinkDto
{
    /// <summary>Link identifier as a string, because the value is a 64 bit Snowflake.</summary>
    public required string Id { get; init; }

    /// <summary>Path the application should open, for example <c>/promo/autumn</c>.</summary>
    public string? DeeplinkPath { get; init; }

    /// <summary>Campaign name carried by the link.</summary>
    public string? Campaign { get; init; }

    /// <summary>Human readable link title.</summary>
    public string? Title { get; init; }
}

/// <summary>
/// Body of <c>POST /v1/events</c>. Batched on purpose: an SDK on a mobile network should spend one
/// request, not one per event (FR-226).
/// </summary>
public sealed record EventBatchDto
{
    /// <summary>Maximum number of events accepted in one batch.</summary>
    public const int MaxEventsPerBatch = 100;

    /// <summary>Installation the events belong to.</summary>
    public required string InstallId { get; init; }

    /// <summary>Client platform, for events that arrive before a resolve has happened.</summary>
    public string? Platform { get; init; }

    /// <summary>Host application version.</summary>
    public string? AppVersion { get; init; }

    /// <summary>The events. At most <see cref="MaxEventsPerBatch"/> per request.</summary>
    public required IReadOnlyList<EventDto> Events { get; init; }
}

/// <summary>
/// A single SDK event. <c>link_open</c> is the important one: when the operating system opens the
/// application directly through a Universal or App Link, no HTTP request ever reaches the engine,
/// so without this report the click is invisible and the most successful campaigns are the ones
/// most under-counted (FR-223, §B.6.4).
/// </summary>
public sealed record EventDto
{
    /// <summary>Event type: <c>link_open</c>, <c>first_open</c>, <c>session</c>, <c>conversion</c>
    /// or <c>custom</c>.</summary>
    public required string Type { get; init; }

    /// <summary>Name of a conversion or custom event, for example <c>purchase</c>.</summary>
    public string? Name { get; init; }

    /// <summary>URL that opened the application, for a <c>link_open</c> event.</summary>
    public string? Url { get; init; }

    /// <summary>Monetary value of a conversion.</summary>
    public decimal? Value { get; init; }

    /// <summary>ISO 4217 currency code accompanying <see cref="Value"/>.</summary>
    public string? Currency { get; init; }

    /// <summary>When the event happened on the device. Absent means "on arrival".</summary>
    public DateTimeOffset? Ts { get; init; }

    /// <summary>Additional event properties.</summary>
    public IReadOnlyDictionary<string, string>? Properties { get; init; }
}

/// <summary>Acknowledgement of an accepted event batch.</summary>
public sealed record EventBatchAcceptedDto
{
    /// <summary>How many events were accepted for processing.</summary>
    public required int Accepted { get; init; }

    /// <summary>How many were rejected, for example for an unknown type or a stale timestamp.</summary>
    public int Rejected { get; init; }
}
