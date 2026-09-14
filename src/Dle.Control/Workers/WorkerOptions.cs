using System.ComponentModel.DataAnnotations;

namespace Dle.Control.Workers;

/// <summary>
/// Configuration of the background workers, bound from <c>Dle:Workers</c> (§B.3, §C.8).
/// </summary>
/// <remarks>
/// <para>
/// Every worker can be switched off individually, and that is not a convenience. A deployment that
/// splits the control plane into an API tier and a jobs tier runs the same image twice with
/// different switches; a deployment doing a delicate migration turns the retention job off for a
/// week; a development machine turns everything off so that starting the API does not start
/// reaching out to the internet. The alternative — a single "run workers" flag — forces all of
/// those into all-or-nothing.
/// </para>
/// <para>
/// The intervals are deliberately coarse. None of these jobs is latency sensitive: the nightly
/// checks are nightly because §C.8 says nightly, the rollup follows the click stream by minutes
/// because the watermark already refuses to aggregate a bucket that is still filling, and the
/// dispatcher polls in seconds because a webhook that arrives a minute late is a webhook a customer
/// notices.
/// </para>
/// </remarks>
public sealed class WorkerOptions
{
    /// <summary>Configuration section this type binds to.</summary>
    public const string SectionName = "Dle:Workers";

    /// <summary>
    /// Master switch. With this off no worker is registered at all, which is what an API-only
    /// replica wants.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Longest a worker waits to take the leader lock before giving up for this tick, in seconds.
    /// </summary>
    /// <remarks>
    /// The lock is taken with <c>pg_try_advisory_lock</c>, which never blocks, so this bounds only
    /// the connection and the round trip. A worker that cannot reach the database simply does not
    /// run, and says so.
    /// </remarks>
    [Range(1, 120)]
    public int LockTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Random delay, in seconds, added before a worker's first tick.
    /// </summary>
    /// <remarks>
    /// Replicas start together — a rolling deploy finishes within seconds — so without jitter every
    /// replica contends for every lock at the same instant, and every job's first pass is a
    /// thundering herd against the same rows.
    /// </remarks>
    [Range(0, 600)]
    public int StartupJitterSeconds { get; set; } = 30;

    /// <summary>Nightly verification of the customers' association files (§C.8, FR-143, T-04).</summary>
    public DomainVerificationWorkerOptions DomainVerification { get; set; } = new();

    /// <summary>Periodic aggregation into the reporting tables (§B.3 component C-09).</summary>
    public ScheduledWorkerOptions Rollup { get; set; } = new() { IntervalMinutes = 5 };

    /// <summary>Retention and anonymisation of the click stream (FR-247, §E.6.3).</summary>
    public ScheduledWorkerOptions Retention { get; set; } = new() { IntervalMinutes = 6 * 60 };

    /// <summary>Draining, signing and delivering the webhook outbox (§B.3 component C-10).</summary>
    public ScheduledWorkerOptions WebhookDispatch { get; set; } = new() { IntervalMinutes = 0 };

    /// <summary>Nightly re-check of the targets of active links (§E.3 step 5).</summary>
    public UrlReputationWorkerOptions UrlReputation { get; set; } = new();

    /// <summary>Out of band refresh of the GeoIP database (§C.4 <c>Dle:Edge:GeoIp</c>).</summary>
    public GeoIpUpdateWorkerOptions GeoIpUpdate { get; set; } = new();
}

/// <summary>Settings shared by every scheduled worker.</summary>
public class ScheduledWorkerOptions
{
    /// <summary>Whether this worker runs.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Interval between passes, in minutes. Zero means "use the module's own interval", which is
    /// how the webhook dispatcher follows <c>Dle:Webhooks:PollIntervalSeconds</c> instead of
    /// carrying a second number that can disagree with it.
    /// </summary>
    [Range(0, 43200)]
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>Whether a pass runs immediately at startup rather than after the first interval.</summary>
    public bool RunAtStartup { get; set; } = true;
}

/// <summary>Settings of the nightly domain verification worker.</summary>
public sealed class DomainVerificationWorkerOptions : ScheduledWorkerOptions
{
    /// <summary>Creates the options with the nightly default of §C.8.</summary>
    public DomainVerificationWorkerOptions()
    {
        IntervalMinutes = 24 * 60;
        RunAtStartup = false;
    }

    /// <summary>Timeout of one association file fetch, in seconds.</summary>
    [Range(1, 120)]
    public int FetchTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// How many redirects are followed while fetching, purely so the count can be reported.
    /// </summary>
    /// <remarks>
    /// Both Apple and Google refuse a redirected association file, so any value above zero is a
    /// failure. The fetch follows a couple anyway in order to say <em>what</em> is at the end of
    /// them, because "your file redirects to your marketing site" is a far more useful diagnosis
    /// than "your file is missing" (§A.2.1, §A.2.2, TC-124).
    /// </remarks>
    [Range(0, 10)]
    public int MaxRedirects { get; set; } = 3;

    /// <summary>Largest association file that is read, in bytes.</summary>
    [Range(1024, 4 * 1024 * 1024)]
    public int MaxDocumentBytes { get; set; } = 256 * 1024;

    /// <summary>How many domains are verified in one pass.</summary>
    [Range(1, 100_000)]
    public int MaxDomainsPerRun { get; set; } = 1_000;

    /// <summary>
    /// Whether an unresolvable host is treated as a possible subdomain takeover (T-04).
    /// </summary>
    /// <remarks>
    /// On. A host that stops resolving is the visible signature of a dangling CNAME: the record
    /// still points at a provider, the resource behind it is gone, and anybody who claims that name
    /// at the provider inherits the domain — association files, deep links and all. It is reported
    /// as a failed <c>dns</c> check rather than as a transient error, because treating it as
    /// transient is how a takeover goes unnoticed for a month.
    /// </remarks>
    public bool ReportUnresolvableAsTakeoverRisk { get; set; } = true;
}

/// <summary>Settings of the nightly URL reputation re-check.</summary>
public sealed class UrlReputationWorkerOptions : ScheduledWorkerOptions
{
    /// <summary>Creates the options with the nightly default of §E.3 step 5.</summary>
    public UrlReputationWorkerOptions()
    {
        IntervalMinutes = 24 * 60;
        RunAtStartup = false;
    }

    /// <summary>How many links are re-checked in one pass.</summary>
    /// <remarks>
    /// A bound rather than a target. The pass walks active links oldest-checked first, so a large
    /// instance simply takes several nights to come round — which is fine, and far better than one
    /// pass that holds a connection for an hour.
    /// </remarks>
    [Range(1, 1_000_000)]
    public int MaxLinksPerRun { get; set; } = 2_000;

    /// <summary>How many links are checked concurrently.</summary>
    [Range(1, 64)]
    public int Concurrency { get; set; } = 4;
}

/// <summary>Settings of the GeoIP database refresh.</summary>
/// <remarks>
/// Disabled unless a download URL is configured, because the database this fetches is licensed and
/// the licence is the deployment's, not the engine's. MaxMind requires an account and a licence key
/// for GeoLite2; an air-gapped or licence-averse deployment ships the file by other means and
/// leaves this off, which is why the edge treats a missing database as "no geography" rather than
/// as an error.
/// </remarks>
public sealed class GeoIpUpdateWorkerOptions : ScheduledWorkerOptions
{
    /// <summary>Creates the options with a weekly default.</summary>
    public GeoIpUpdateWorkerOptions()
    {
        Enabled = false;
        IntervalMinutes = 7 * 24 * 60;
        RunAtStartup = false;
    }

    /// <summary>
    /// URL the database archive is downloaded from. Empty means the worker does nothing.
    /// </summary>
    public string? DownloadUrl { get; set; }

    /// <summary>Licence key appended to the download URL, when the provider needs one.</summary>
    public string? LicenseKey { get; set; }

    /// <summary>Path of the database file the edge reads, matching <c>Dle:Edge:GeoIp:Path</c>.</summary>
    public string? DatabasePath { get; set; }

    /// <summary>Timeout of the download, in seconds.</summary>
    [Range(1, 3600)]
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Largest database archive accepted, in bytes.</summary>
    [Range(1024, 1024L * 1024 * 1024)]
    public long MaxDownloadBytes { get; set; } = 256L * 1024 * 1024;
}
