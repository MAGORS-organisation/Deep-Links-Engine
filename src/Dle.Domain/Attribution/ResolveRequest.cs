using Dle.Domain.Clients;
using Dle.Domain.Privacy;

namespace Dle.Domain.Attribution;

/// <summary>
/// Transport neutral form of a <c>POST /v1/resolve</c> call (§B.7.2): an installation asking the
/// engine which click, if any, brought it here.
/// </summary>
/// <remarks>
/// The strategies are tried in the order configured for the tenant, strongest first (ADR-008):
/// <see cref="Referrer"/> on Android, <see cref="LoginKey"/> and <see cref="ClaimCode"/> on both
/// platforms, and only then <see cref="Signals"/>. The probabilistic strategy is disabled by
/// default and requires a recorded consent, so <see cref="Consent"/> is an input to the decision
/// rather than a filter applied afterwards.
/// </remarks>
public sealed record ResolveRequest
{
    /// <summary>SDK generated identifier, stable for the lifetime of one installation. The
    /// deduplication key that makes a repeated resolve idempotent (TC-143, FR-188).</summary>
    public required string InstallId { get; init; }

    /// <summary>Platform the SDK runs on.</summary>
    public required Platform Platform { get; init; }

    /// <summary>Version of the host application, for example <c>3.4.1</c>.</summary>
    public string? AppVersion { get; init; }

    /// <summary>Operating system version, for example <c>15</c>.</summary>
    public string? OsVersion { get; init; }

    /// <summary>
    /// Raw Android Play Install Referrer string, exactly as the platform returned it. It is
    /// stored verbatim for auditing and parsed by <see cref="InstallReferrerParser"/>; it is
    /// never interpreted as a URL and never used to build a redirect target.
    /// </summary>
    public string? Referrer { get; init; }

    /// <summary>Claim code the user typed into the application (S3).</summary>
    public string? ClaimCode { get; init; }

    /// <summary>
    /// Hash of the user account identifier, used to reconcile an anonymous web session with a
    /// login (S2). The SDK sends a hash, never the account identifier itself.
    /// </summary>
    public string? LoginKey { get; init; }

    /// <summary>Device signals for the probabilistic strategy. Ignored unless the consent
    /// decision allows probabilistic matching.</summary>
    public DeviceSignals? Signals { get; init; }

    /// <summary>Consent signal reported by the SDK, with the timestamp that documents it.</summary>
    public ConsentSignal? Consent { get; init; }

    /// <summary>Instant the request reached the server, in UTC. Together with the click
    /// timestamp this yields the elapsed time that decays the probabilistic confidence.</summary>
    public required DateTimeOffset ReceivedAt { get; init; }
}
