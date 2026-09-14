using System.Collections.ObjectModel;

using Dle.Control.Features.Domains;
using Dle.Control.Features.Webhooks;
using Dle.Domain.Contracts;
using Dle.Domain.Entities;
using Dle.Domain.Ports;
using Dle.Persistence.Repositories;
using Dle.Persistence.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dle.Control.Workers;

/// <summary>
/// Verifies every registered domain's association files and DNS every night (§C.8, FR-143, T-04).
/// </summary>
/// <remarks>
/// <para>
/// This is the worker that catches the failure mode nobody notices. Association files break
/// silently: the customer's CDN starts redirecting, a content type changes from
/// <c>application/json</c> to <c>text/html</c>, a certificate expires, a new build is signed with
/// the upload certificate instead of the Play App Signing one. In every case deep links simply stop
/// opening the application and start opening a browser. Nothing errors, nothing logs, and the
/// customer's first signal is a drop in a conversion metric weeks later. §C.8 puts this on a
/// nightly schedule for exactly that reason.
/// </para>
/// <para>
/// The checks themselves are <see cref="DomainVerificationService"/> — the same code the
/// <c>POST /api/v1/domains/{id}/verify</c> endpoint runs when an operator presses the button. That
/// is deliberate and is the only sane arrangement: if the nightly pass had its own implementation,
/// the two would disagree eventually, and the operator pressing "verify" to reproduce a nightly
/// failure would get a green tick.
/// </para>
/// <para>
/// What this worker adds on top is the part only a scheduled pass can see. A host that stops
/// resolving <em>after</em> it has verified successfully is the visible signature of T-04, subdomain
/// takeover: the customer pointed <c>link.customer.example</c> at a hosting provider with a CNAME,
/// later removed the resource but left the record, and anybody who now claims that name at the
/// provider inherits the domain — including its association files, which means they inherit the
/// ability to open the customer's application. A single manual verification cannot tell that apart
/// from a domain that was never set up; a nightly pass that remembers yesterday can. It is raised
/// as a takeover risk rather than as a transient network blip, because treating it as transient is
/// precisely how a takeover stays unnoticed for a month.
/// </para>
/// <para>
/// Every result is appended as a <c>domain_verifications</c> row rather than only summarised on the
/// domain, so the question an intermittent failure actually raises — "since when?" — has an answer.
/// A domain that fails also raises <c>domain.verification_failed</c> on the webhook outbox, so the
/// customer learns from their own systems rather than from ours.
/// </para>
/// </remarks>
public sealed partial class DomainVerificationWorker : LeaderElectedBackgroundService
{
    /// <summary>Job name, used as the advisory lock key and the metric tag.</summary>
    public const string Job = "dle.worker.domain-verification";

    /// <summary>Issue code the verification service raises when a host does not resolve.</summary>
    private const string DnsUnresolvedCode = "dns.unresolved";

    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<WorkerOptions> _options;
    private readonly ILogger<DomainVerificationWorker> _logger;

    /// <summary>
    /// Creates the worker.
    /// </summary>
    /// <param name="scopes">Scope factory; every pass resolves its own scoped services.</param>
    /// <param name="leader">Leader election.</param>
    /// <param name="metrics">Instruments.</param>
    /// <param name="options">Worker options.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public DomainVerificationWorker(
        IServiceScopeFactory scopes,
        PostgresLeaderLock leader,
        WorkerMetrics metrics,
        IOptionsMonitor<WorkerOptions> options,
        TimeProvider timeProvider,
        ILogger<DomainVerificationWorker> logger)
        : base(leader, metrics, timeProvider, logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override string JobName => Job;

    /// <inheritdoc />
    protected override bool IsEnabled =>
        _options.CurrentValue.Enabled && _options.CurrentValue.DomainVerification.Enabled;

    /// <inheritdoc />
    protected override TimeSpan Interval =>
        TimeSpan.FromMinutes(Math.Max(_options.CurrentValue.DomainVerification.IntervalMinutes, 1));

    /// <inheritdoc />
    protected override bool RunAtStartup => _options.CurrentValue.DomainVerification.RunAtStartup;

    /// <inheritdoc />
    protected override TimeSpan LockTimeout => TimeSpan.FromSeconds(_options.CurrentValue.LockTimeoutSeconds);

    /// <inheritdoc />
    protected override TimeSpan StartupJitter => TimeSpan.FromSeconds(_options.CurrentValue.StartupJitterSeconds);

    /// <inheritdoc />
    protected override async Task<string> RunPassAsync(CancellationToken cancellationToken)
    {
        DomainVerificationWorkerOptions options = _options.CurrentValue.DomainVerification;

        using IServiceScope scope = _scopes.CreateScope();

        DleDbContext db = scope.ServiceProvider.GetRequiredService<DleDbContext>();
        ITenantContext tenants = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        DomainRepository domains = scope.ServiceProvider.GetRequiredService<DomainRepository>();
        DomainVerificationService verifier = scope.ServiceProvider.GetRequiredService<DomainVerificationService>();
        IWebhookOutbox outbox = scope.ServiceProvider.GetRequiredService<IWebhookOutbox>();

        List<LinkDomain> registered = await ListDomainsAsync(db, options.MaxDomainsPerRun, cancellationToken);

        int checkedDomains = 0;
        int failing = 0;
        int takeoverRisks = 0;

        foreach (LinkDomain domain in registered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Every write below is a tenant owned row, so the pass enters the owning tenant's scope
            // rather than dropping the filter. Cross-tenant reading is a listing concern; writing is
            // never one (T-09).
            using IDisposable tenantScope = tenants.BeginScope(domain.TenantId);

            DomainVerificationRun run = await verifier.VerifyAsync(domain, cancellationToken);

            foreach (DomainVerification record in run.Runs)
            {
                _ = await domains.RecordVerificationAsync(record, cancellationToken);
            }

            checkedDomains++;

            if (run.Response.Ok)
            {
                continue;
            }

            failing++;

            bool takeoverRisk = options.ReportUnresolvableAsTakeoverRisk && LooksLikeTakeover(domain, run.Response);

            if (takeoverRisk)
            {
                takeoverRisks++;
                LogTakeoverRisk(_logger, domain.Host, domain.LastVerifiedAt);
            }

            await AnnounceAsync(domain, run.Response, takeoverRisk, outbox, Clock.GetUtcNow(), cancellationToken);
        }

        Metrics.Items(Job, "domain", checkedDomains);
        Metrics.Items(Job, "takeover_risk", takeoverRisks);
        Metrics.ReportDomainVerificationFailures(failing);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{checkedDomains} domains verified, {failing} failing, {takeoverRisks} possible takeovers.");
    }

    /// <summary>
    /// Whether a failure looks like a subdomain takeover rather than a misconfiguration (T-04).
    /// </summary>
    /// <param name="domain">The domain as it stood before this pass.</param>
    /// <param name="response">What this pass found.</param>
    /// <returns><see langword="true"/> when a previously verified host has stopped resolving.</returns>
    /// <remarks>
    /// The two conditions together are what make this a signal rather than noise. A host that has
    /// never resolved is a customer who has not finished setting up their DNS; a host that resolved
    /// yesterday and does not resolve today, while the domain is still active and still serving
    /// links, is a record whose target has been removed. The second case is the one where somebody
    /// else can claim the name at the provider and inherit the association files.
    /// </remarks>
    private static bool LooksLikeTakeover(LinkDomain domain, DomainVerificationResponse response)
    {
        if (domain.LastVerifiedAt is null)
        {
            return false;
        }

        foreach (VerificationCheckResult check in response.Checks)
        {
            if (!string.Equals(check.Kind, DomainVerificationService.DnsKind, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(check.Status, DomainVerificationService.FailedStatus, StringComparison.Ordinal))
            {
                return false;
            }

            foreach (string code in check.Codes)
            {
                if (string.Equals(code, DnsUnresolvedCode, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Puts <c>domain.verification_failed</c> on the webhook outbox.</summary>
    /// <param name="domain">The domain that failed.</param>
    /// <param name="response">What the verification found.</param>
    /// <param name="takeoverRisk">Whether the failure looks like a subdomain takeover (T-04).</param>
    /// <param name="outbox">Transactional webhook outbox.</param>
    /// <param name="now">The current instant, from the injected clock (SHARED-KERNEL §17.2).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static async Task AnnounceAsync(
        LinkDomain domain,
        DomainVerificationResponse response,
        bool takeoverRisk,
        IWebhookOutbox outbox,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> data = new(StringComparer.Ordinal)
        {
            ["domain_id"] = domain.Id.ToString(),
            ["host"] = domain.Host,
            ["takeover_risk"] = takeoverRisk ? "true" : "false",
        };

        foreach (VerificationCheckResult check in response.Checks)
        {
            data[check.Kind] = check.Status;

            if (check.Codes.Count > 0)
            {
                data[check.Kind + "_codes"] = string.Join(",", check.Codes);
            }
        }

        string payload = WebhookEventTypes.Render(
            WebhookEventTypes.DomainVerificationFailed,
            Guid.CreateVersion7(now),
            now,
            new ReadOnlyDictionary<string, string>(data));

        await outbox.EnqueueAsync(
            domain.TenantId,
            WebhookEventTypes.DomainVerificationFailed,
            payload,
            cancellationToken);
    }

    /// <summary>Lists the domains to verify, least recently verified first.</summary>
    /// <remarks>
    /// Ordered so that a large instance whose pass is bounded still comes round to every domain: the
    /// ones checked longest ago go first, and a domain never verified sorts before all of them.
    /// </remarks>
    private static async Task<List<LinkDomain>> ListDomainsAsync(
        DleDbContext db,
        int limit,
        CancellationToken cancellationToken)
    {
        using CrossTenantScope scope = db.BeginCrossTenantScope(
            "the nightly verification pass covers every registered domain of the instance (§C.8)");

        return await db.Domains
            .AsNoTracking()
            .AcrossTenants()
            .Where(d => d.IsActive)
            .OrderBy(d => d.LastVerifiedAt.HasValue)
            .ThenBy(d => d.LastVerifiedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    [LoggerMessage(
        EventId = 5620,
        Level = LogLevel.Warning,
        Message = "Host {Host} stopped resolving after verifying successfully at {LastVerifiedAt}. "
            + "If it is a CNAME whose target has been removed, somebody else can claim that name at "
            + "the provider and inherit this domain's association files and deep links (T-04). "
            + "Remove the record or restore the resource.")]
    private static partial void LogTakeoverRisk(ILogger logger, string host, DateTimeOffset? lastVerifiedAt);
}
