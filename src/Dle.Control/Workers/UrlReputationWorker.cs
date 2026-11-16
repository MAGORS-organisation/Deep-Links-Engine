using System.Text.Json;

using Dle.Control.Features.Abuse;
using Dle.Control.Features.Shared;
using Dle.Domain.Abuse;
using Dle.Domain.Ports;
using Dle.Domain.Routing;
using Dle.Domain.Serialization;
using Dle.Persistence.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dle.Control.Workers;

/// <summary>
/// Re-checks the targets of active links against the reputation sources.
/// </summary>
/// <remarks>
/// <para>
/// This worker exists because of one specific abuse pattern, and it is worth naming plainly:
/// <b>the target is changed after the link is created</b>. An abuser registers, creates links to a
/// harmless page, passes every check at creation, waits, and then edits the target to a phishing
/// page — or, more often, points at a page they control and change the content of. A safety check
/// that only runs at creation is the one check such an operator plans around. A nightly re-check is
/// what turns "we checked it once" into "we keep checking it".
/// </para>
/// <para>
/// The default source is URLhaus, and the licensing is the reason rather than the quality. Google
/// Safe Browsing v5 is restricted to non-commercial use and Web Risk is paid, so neither can be the
/// default of an open-source product a commercial self-hoster is meant to run. URLhaus is free and
/// unrestricted. Anything else belongs behind <see cref="IUrlReputationProvider"/>, added by the
/// deployment that holds the licence.
/// </para>
/// <para>
/// A target found malicious is quarantined rather than deleted, through the same service the human
/// triage queue uses, so the decision is audited and the link answers 410 with an explanation
/// instead of vanishing. Whether the quarantine is automatic is configuration:
/// on by default, because the window between a target turning malicious and an operator reading a
/// queue is exactly the window the abuse is designed to fit into.
/// </para>
/// </remarks>
public sealed partial class UrlReputationWorker : LeaderElectedBackgroundService
{
    /// <summary>Job name, used as the advisory lock key and the metric tag.</summary>
    public const string Job = "dle.worker.url-reputation";

    /// <summary>Reason recorded on an automatic quarantine.</summary>
    private const string QuarantineReason =
        "The target of this link was found malicious by an automatic re-check of active links.";

    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<WorkerOptions> _options;
    private readonly IOptionsMonitor<AbuseOptions> _abuse;
    private readonly ILogger<UrlReputationWorker> _logger;

    /// <summary>
    /// Creates the worker.
    /// </summary>
    /// <param name="scopes">Scope factory.</param>
    /// <param name="leader">Leader election.</param>
    /// <param name="metrics">Instruments.</param>
    /// <param name="options">Worker options.</param>
    /// <param name="abuse">Abuse options, which own the re-check interval and the auto quarantine
    /// switch.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public UrlReputationWorker(
        IServiceScopeFactory scopes,
        PostgresLeaderLock leader,
        WorkerMetrics metrics,
        IOptionsMonitor<WorkerOptions> options,
        IOptionsMonitor<AbuseOptions> abuse,
        TimeProvider timeProvider,
        ILogger<UrlReputationWorker> logger)
        : base(leader, metrics, timeProvider, logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(abuse);
        ArgumentNullException.ThrowIfNull(logger);

        _scopes = scopes;
        _options = options;
        _abuse = abuse;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override string JobName => Job;

    /// <inheritdoc />
    protected override bool IsEnabled =>
        _options.CurrentValue.Enabled && _options.CurrentValue.UrlReputation.Enabled;

    /// <inheritdoc />
    /// <remarks>
    /// Follows <c>Dle:Abuse:RecheckIntervalHours</c> unless the worker section overrides it, so the
    /// abuse module owns its own schedule and there are not two numbers to keep in step.
    /// </remarks>
    protected override TimeSpan Interval
    {
        get
        {
            int configured = _options.CurrentValue.UrlReputation.IntervalMinutes;

            return configured > 0
                ? TimeSpan.FromMinutes(configured)
                : TimeSpan.FromHours(Math.Max(_abuse.CurrentValue.RecheckIntervalHours, 1));
        }
    }

    /// <inheritdoc />
    protected override bool RunAtStartup => _options.CurrentValue.UrlReputation.RunAtStartup;

    /// <inheritdoc />
    protected override TimeSpan LockTimeout => TimeSpan.FromSeconds(_options.CurrentValue.LockTimeoutSeconds);

    /// <inheritdoc />
    protected override TimeSpan StartupJitter => TimeSpan.FromSeconds(_options.CurrentValue.StartupJitterSeconds);

    /// <inheritdoc />
    protected override async Task<string> RunPassAsync(CancellationToken cancellationToken)
    {
        UrlReputationWorkerOptions options = _options.CurrentValue.UrlReputation;
        AbuseOptions abuse = _abuse.CurrentValue;

        using IServiceScope scope = _scopes.CreateScope();

        DleDbContext db = scope.ServiceProvider.GetRequiredService<DleDbContext>();
        IUrlSafetyChecker checker = scope.ServiceProvider.GetRequiredService<IUrlSafetyChecker>();
        AbuseLinkLocator locator = scope.ServiceProvider.GetRequiredService<AbuseLinkLocator>();
        LinkQuarantineService quarantine = scope.ServiceProvider.GetRequiredService<LinkQuarantineService>();
        ITenantContext tenants = scope.ServiceProvider.GetRequiredService<ITenantContext>();

        List<LinkTarget> targets = await ListTargetsAsync(db, options.MaxLinksPerRun, cancellationToken);

        int checkedLinks = 0;
        int flagged = 0;
        int quarantined = 0;

        foreach (LinkTarget target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (string url in EnumerateDistinctUrls(target))
            {
                cancellationToken.ThrowIfCancellationRequested();

                UrlSafetyVerdict verdict = await checker.CheckAsync(url, cancellationToken);
                checkedLinks++;

                if (verdict.Level is UrlSafetyLevel.Safe or UrlSafetyLevel.Unknown)
                {
                    continue;
                }

                flagged++;
                LogFlagged(_logger, target.LinkId, verdict.Level.ToString(), verdict.Source);

                if (!abuse.AutoQuarantineOnMaliciousRecheck || !WarrantsQuarantine(verdict))
                {
                    // Recorded and left for a human. Automatic withdrawal on a soft verdict would make
                    // a false positive from a feed into a customer outage.
                    continue;
                }

                using IDisposable tenantScope = tenants.BeginScope(target.TenantId);

                LocatedLink? located = await locator.FindByIdAsync(target.LinkId, cancellationToken);

                if (located is null || located.IsQuarantined)
                {
                    break;
                }

                if (await quarantine.QuarantineAsync(located, QuarantineReason, RequestActor.System, cancellationToken))
                {
                    quarantined++;
                    break;
                }
            }
        }

        Metrics.Items(Job, "link", checkedLinks);
        Metrics.Items(Job, "quarantined_link", quarantined);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{checkedLinks} targets re-checked, {flagged} flagged, {quarantined} quarantined.");
    }

    /// <summary>
    /// Whether a re-check verdict withdraws a live link without a human.
    /// </summary>
    /// <remarks>
    /// Only a verdict about the target's reputation does. A verdict about where the host points
    /// (<see cref="TargetUrlPolicy.AddressSource"/>) is also what a resolver that failed for a
    /// moment produces; acting on it would let one DNS outage during a pass quarantine every link
    /// the pass looked at. It is still counted and logged as flagged.
    /// </remarks>
    /// <param name="verdict">The verdict of the re-check.</param>
    /// <returns><see langword="true"/> when the link should be quarantined.</returns>
    public static bool WarrantsQuarantine(UrlSafetyVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        return verdict.Level >= UrlSafetyLevel.Malicious
            && !string.Equals(verdict.Source, TargetUrlPolicy.AddressSource, StringComparison.Ordinal);
    }

    /// <summary>
    /// Where a bounded pass starts in the list of active links, so that successive passes walk the
    /// whole list instead of re-checking the same first rows.
    /// </summary>
    /// <param name="total">Number of active links.</param>
    /// <param name="limit">Links one pass may check.</param>
    /// <param name="pass">Ordinal of the pass, any monotonically increasing number.</param>
    /// <returns>The number of rows to skip.</returns>
    public static int WindowOffset(int total, int limit, long pass) =>
        total <= limit || limit <= 0 || pass < 0
            ? 0
            : (int)(pass * limit % total);

    /// <summary>
    /// Lists the targets of this pass: one window of the active links, in identifier order.
    /// </summary>
    /// <remarks>
    /// Nothing is written back to a link after it has been checked, so "oldest first" would hand
    /// every pass the same rows and never reach the rest of a large instance. The window instead
    /// moves by one pass worth of rows each time, derived from the clock, which needs no state and
    /// gives every replica the same answer.
    /// </remarks>
    private async Task<List<LinkTarget>> ListTargetsAsync(
        DleDbContext db,
        int limit,
        CancellationToken cancellationToken)
    {
        using CrossTenantScope scope = db.BeginCrossTenantScope(
            "the nightly reputation re-check covers the active links of every tenant");

        IQueryable<Link> active = db.Links
            .AsNoTracking()
            .AcrossTenants()
            .Where(l => l.IsActive && l.QuarantinedAt == null);

        int total = await active.CountAsync(cancellationToken);
        long pass = Clock.GetUtcNow().ToUnixTimeSeconds() / Math.Max((long)Interval.TotalSeconds, 1L);

        return await active
            .OrderBy(l => l.Id)
            .Skip(WindowOffset(total, limit, pass))
            .Take(limit)
            .Select(l => new LinkTarget(l.Id, l.TenantId, l.TargetUrl, l.ExpiredUrl, l.RoutingRules))
            .ToListAsync(cancellationToken);
    }

    private static IEnumerable<string> EnumerateDistinctUrls(LinkTarget target)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(target.TargetUrl) && seen.Add(target.TargetUrl))
        {
            yield return target.TargetUrl;
        }

        if (!string.IsNullOrWhiteSpace(target.ExpiredUrl) && seen.Add(target.ExpiredUrl))
        {
            yield return target.ExpiredUrl;
        }

        if (string.IsNullOrWhiteSpace(target.RoutingRules))
        {
            yield break;
        }

        RoutingRule[]? rules = null;

        try
        {
            rules = JsonSerializer.Deserialize(target.RoutingRules, DleDomainJsonContext.Default.RoutingRuleArray);
        }
        catch (JsonException)
        {
            yield break;
        }

        if (rules is null)
        {
            yield break;
        }

        foreach (RoutingRule rule in rules)
        {
            if (rule.Then is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(rule.Then.Url) && seen.Add(rule.Then.Url))
            {
                yield return rule.Then.Url;
            }

            if (!string.IsNullOrWhiteSpace(rule.Then.StoreUrl) && seen.Add(rule.Then.StoreUrl))
            {
                yield return rule.Then.StoreUrl;
            }
        }
    }

    /// <summary>One link's targets, reduced to what the re-check needs.</summary>
    /// <param name="LinkId">The link.</param>
    /// <param name="TenantId">Owning tenant, entered before anything is written.</param>
    /// <param name="TargetUrl">Primary fallback URL to re-check.</param>
    /// <param name="ExpiredUrl">Optional expiry destination URL to re-check.</param>
    /// <param name="RoutingRules">Optional serialized routing rules carrying additional web or store URLs.</param>
    private sealed record LinkTarget(
        long LinkId,
        Guid TenantId,
        string TargetUrl,
        string? ExpiredUrl,
        string? RoutingRules);

    [LoggerMessage(
        EventId = 5650,
        Level = LogLevel.Warning,
        Message = "The target of link {LinkId} was re-checked and came back {Level} from {Source}.")]
    private static partial void LogFlagged(ILogger logger, long linkId, string level, string source);
}
