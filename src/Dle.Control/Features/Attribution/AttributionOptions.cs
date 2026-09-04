using System.Globalization;

using Microsoft.Extensions.Options;

namespace Dle.Control.Features.Attribution;

/// <summary>
/// Configuration of the attribution module, bound from <c>Dle:Attribution</c> (§C.4,
/// SHARED-KERNEL §16).
/// </summary>
/// <remarks>
/// <para>
/// The strategy order is configuration rather than code because it is a per-deployment product
/// decision: an Android-only customer never wants the claim code prompt, and an iOS-first customer
/// wants it first. The default order is the one ADR-008 tabulates, strongest first, and
/// <c>probabilistic</c> is deliberately absent from it. Enabling the probabilistic module is
/// therefore two decisions, not one: it has to be switched on <em>and</em> named in the order.
/// </para>
/// <para>
/// Every window here is short on purpose. §A.2.5 reports that probabilistic accuracy is roughly a
/// coin flip beyond 24 hours, so the 60 minute default is a deliberate departure from the seven day
/// windows commercial vendors quote.
/// </para>
/// </remarks>
public sealed class AttributionOptions
{
    /// <summary>Configuration section this type binds to.</summary>
    public const string SectionName = "Dle:Attribution";

    /// <summary>
    /// Order applied when <see cref="Strategies"/> is empty: the three deterministic strategies,
    /// strongest first. <c>probabilistic</c> is not part of it (ADR-008, §C.4).
    /// </summary>
    public static readonly string[] DefaultStrategies =
    [
        AttributionStrategyNames.InstallReferrer,
        AttributionStrategyNames.Login,
        AttributionStrategyNames.ClaimCode,
    ];

    /// <summary>
    /// Strategy names in the order they are tried; the first one that matches wins. Recognised
    /// values are <c>install_referrer</c>, <c>login</c>, <c>claim_code</c> and
    /// <c>probabilistic</c>. An empty list means <see cref="DefaultStrategies"/>.
    /// </summary>
    public IList<string> Strategies { get; } = [];

    /// <summary>Settings of the probabilistic strategy (S4).</summary>
    public ProbabilisticOptions Probabilistic { get; set; } = new();

    /// <summary>Settings of the claim code strategy (S3).</summary>
    public ClaimCodeOptions ClaimCode { get; set; } = new();

    /// <summary>
    /// Half-width, in minutes, of the <c>occurred_at</c> window used to look a click up by its
    /// identifier. The identifier carries its own timestamp, and this window is what lets the
    /// planner prune the partitions of <c>click_events</c> (§B.6.3). Widening it costs a partition
    /// per day of width.
    /// </summary>
    public int ClickWindowMinutes { get; set; } = 5;

    /// <summary>
    /// Oldest install referrer that is still honoured, in days. Play keeps the referrer for about
    /// 90 days (§A.2.4); anything older is a replay rather than an install.
    /// </summary>
    public int MaxReferrerAgeDays { get; set; } = 90;

    /// <summary>
    /// How far back login reconciliation looks for a click carrying the same account hash, in
    /// hours.
    /// </summary>
    public int LoginWindowHours { get; set; } = 24;

    /// <summary>
    /// How long a verified SDK key stays cached, in seconds. Verification is Argon2id and therefore
    /// deliberately expensive; without a cache the event endpoint would spend its whole budget on
    /// it. A revoked key keeps working for at most this long.
    /// </summary>
    public int SdkKeyCacheSeconds { get; set; } = 300;

    /// <summary>Largest request body accepted by the SDK endpoints, in bytes.</summary>
    public int MaxRequestBytes { get; set; } = 128 * 1024;

    /// <summary>Longest accepted installation identifier, in characters.</summary>
    public int MaxInstallIdLength { get; set; } = 128;

    /// <summary>
    /// Returns the configured strategy order, or the default order when none is configured.
    /// </summary>
    /// <returns>The parsed order. Unknown names have already been rejected at startup by
    /// <see cref="AttributionOptionsValidator"/>, so this method never has to guess.</returns>
    public IReadOnlyList<AttributionStrategy> ResolveOrder()
    {
        IList<string> configured = Strategies.Count == 0 ? DefaultStrategies : Strategies;
        var order = new List<AttributionStrategy>(configured.Count);

        foreach (string name in configured)
        {
            AttributionStrategy strategy = AttributionStrategyNames.Parse(name);

            if (strategy != AttributionStrategy.Unknown && !order.Contains(strategy))
            {
                order.Add(strategy);
            }
        }

        return order;
    }
}

/// <summary>
/// Settings of the probabilistic strategy, bound from <c>Dle:Attribution:Probabilistic</c>.
/// </summary>
/// <remarks>
/// Off by default. This is the one strategy that guesses, and ADR-008 makes it opt-in for a legal
/// reason (ePrivacy art. 5(3), EDPB Guidelines 2/2023) as much as for a technical one.
/// </remarks>
public sealed class ProbabilisticOptions
{
    /// <summary>Whether the module may run at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Width of the matching window in minutes. Confidence decays linearly to zero across it, and
    /// past its end the answer is <c>none</c> rather than a weak match (TC-147).
    /// </summary>
    public int WindowMinutes { get; set; } = 60;

    /// <summary>Lowest confidence that is still reported as a match.</summary>
    public decimal MinConfidence { get; set; } = 0.55m;

    /// <summary>
    /// Whether documented attribution consent is required. The engine only accepts
    /// <see langword="true"/>; see <see cref="AttributionOptionsValidator"/> for why.
    /// </summary>
    public bool RequireConsent { get; set; } = true;

    /// <summary>Window as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan Window => TimeSpan.FromMinutes(WindowMinutes);
}

/// <summary>
/// Settings of the claim code strategy, bound from <c>Dle:Attribution:ClaimCode</c>.
/// </summary>
public sealed class ClaimCodeOptions
{
    /// <summary>Whether codes may be issued and redeemed.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Time to live of an issued code, in minutes. A six character code carries about 28 bits, so
    /// the short life is not convenience but the control that makes the code safe to use at all
    /// (§E.4.1, K3).
    /// </summary>
    public int TtlMinutes { get; set; } = 60;

    /// <summary>Time to live as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan Ttl => TimeSpan.FromMinutes(TtlMinutes);
}

/// <summary>
/// Rate limits of the SDK endpoints, bound from <c>Dle:RateLimits</c> (§E.9).
/// </summary>
/// <remarks>
/// The defaults are the table in §E.9 verbatim. The resolve limit looks brutally low until you
/// notice that a correct SDK calls it once in the lifetime of an installation; five per hour is
/// already four retries of headroom.
/// </remarks>
public sealed class AttributionRateLimitOptions
{
    /// <summary>Configuration section this type binds to.</summary>
    public const string SectionName = "Dle:RateLimits";

    /// <summary>Permitted <c>POST /v1/resolve</c> calls per installation and window.</summary>
    public int ResolvePermitLimit { get; set; } = 5;

    /// <summary>Width of the resolve window, in minutes.</summary>
    public int ResolveWindowMinutes { get; set; } = 60;

    /// <summary>Bucket size for <c>POST /v1/events</c>: the burst an installation may spend at once.</summary>
    public int EventsBurstLimit { get; set; } = 120;

    /// <summary>Tokens added to the event bucket each replenishment period.</summary>
    public int EventsTokensPerPeriod { get; set; } = 60;

    /// <summary>Replenishment period of the event bucket, in seconds.</summary>
    public int EventsPeriodSeconds { get; set; } = 60;

    /// <summary>Credential verifications permitted per caller address and window (§E.9, last row).</summary>
    public int AuthenticationPermitLimit { get; set; } = 10;

    /// <summary>Width of the authentication window, in seconds.</summary>
    public int AuthenticationWindowSeconds { get; set; } = 60;

    /// <summary>Claim codes a caller may have issued per window.</summary>
    public int ClaimCodeIssuePermitLimit { get; set; } = 60;

    /// <summary>Width of the claim code issue window, in seconds.</summary>
    public int ClaimCodeIssueWindowSeconds { get; set; } = 60;
}

/// <summary>
/// Startup validation of <see cref="AttributionOptions"/> and
/// <see cref="AttributionRateLimitOptions"/>.
/// </summary>
/// <remarks>
/// Written by hand rather than with data annotations for the same reason the crypto module does it:
/// the interesting rules are relationships between values, and one of them is a legal rule that
/// deserves to be spelled out where an operator will read it.
/// </remarks>
public sealed class AttributionOptionsValidator
    : IValidateOptions<AttributionOptions>, IValidateOptions<AttributionRateLimitOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AttributionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        foreach (string strategy in options.Strategies)
        {
            if (AttributionStrategyNames.Parse(strategy) == AttributionStrategy.Unknown)
            {
                failures.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Dle:Attribution:Strategies contains '{strategy}', which is not one of " +
                    $"install_referrer, login, claim_code, probabilistic."));
            }
        }

        Range(failures, "Dle:Attribution:ClickWindowMinutes", options.ClickWindowMinutes, 1, 1440);
        Range(failures, "Dle:Attribution:MaxReferrerAgeDays", options.MaxReferrerAgeDays, 1, 400);
        Range(failures, "Dle:Attribution:LoginWindowHours", options.LoginWindowHours, 1, 720);
        Range(failures, "Dle:Attribution:SdkKeyCacheSeconds", options.SdkKeyCacheSeconds, 0, 3600);
        Range(failures, "Dle:Attribution:MaxRequestBytes", options.MaxRequestBytes, 1024, 4 * 1024 * 1024);
        Range(failures, "Dle:Attribution:MaxInstallIdLength", options.MaxInstallIdLength, 8, 512);
        Range(failures, "Dle:Attribution:Probabilistic:WindowMinutes", options.Probabilistic.WindowMinutes, 1, 1440);
        Range(failures, "Dle:Attribution:ClaimCode:TtlMinutes", options.ClaimCode.TtlMinutes, 1, 1440);

        if (options.Probabilistic.MinConfidence is <= 0m or > 1m)
        {
            failures.Add("Dle:Attribution:Probabilistic:MinConfidence must be greater than 0 and at most 1.");
        }

        // The one value the engine refuses to obey. Turning consent off would make the module
        // process device signals without a legal basis under ePrivacy art. 5(3), which EDPB
        // Guidelines 2/2023 confirms covers marketing attribution. Refusing to start is the
        // fail-closed answer (SHARED-KERNEL §17.9); silently ignoring the setting would be worse,
        // because the operator would believe it took effect.
        if (options.Probabilistic is { Enabled: true, RequireConsent: false })
        {
            failures.Add(
                "Dle:Attribution:Probabilistic:RequireConsent must stay true while the module is " +
                "enabled. Probabilistic matching processes device signals, for which ePrivacy " +
                "art. 5(3) and EDPB Guidelines 2/2023 require documented consent; this engine " +
                "will not run it without one.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AttributionRateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        Range(failures, "Dle:RateLimits:ResolvePermitLimit", options.ResolvePermitLimit, 1, 10_000);
        Range(failures, "Dle:RateLimits:ResolveWindowMinutes", options.ResolveWindowMinutes, 1, 1440);
        Range(failures, "Dle:RateLimits:EventsBurstLimit", options.EventsBurstLimit, 1, 100_000);
        Range(failures, "Dle:RateLimits:EventsTokensPerPeriod", options.EventsTokensPerPeriod, 1, 100_000);
        Range(failures, "Dle:RateLimits:EventsPeriodSeconds", options.EventsPeriodSeconds, 1, 3600);
        Range(failures, "Dle:RateLimits:AuthenticationPermitLimit", options.AuthenticationPermitLimit, 1, 10_000);
        Range(failures, "Dle:RateLimits:AuthenticationWindowSeconds", options.AuthenticationWindowSeconds, 1, 3600);
        Range(failures, "Dle:RateLimits:ClaimCodeIssuePermitLimit", options.ClaimCodeIssuePermitLimit, 1, 100_000);
        Range(failures, "Dle:RateLimits:ClaimCodeIssueWindowSeconds", options.ClaimCodeIssueWindowSeconds, 1, 3600);

        if (options.EventsTokensPerPeriod > options.EventsBurstLimit)
        {
            failures.Add(
                "Dle:RateLimits:EventsTokensPerPeriod must not exceed " +
                "Dle:RateLimits:EventsBurstLimit; a bucket cannot hold more than its size.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void Range(List<string> failures, string key, int value, int minimum, int maximum)
    {
        if (value < minimum || value > maximum)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{key} must be between {minimum} and {maximum}; it is {value}."));
        }
    }
}
