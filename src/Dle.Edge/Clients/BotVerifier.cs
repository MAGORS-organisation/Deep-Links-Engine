using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;

using Dle.Edge.Configuration;
using Dle.Edge.Telemetry;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dle.Edge.Clients;

/// <summary>
/// Confirms — or fails to confirm — a user agent's claim to be a crawler, by reverse DNS with forward
/// confirmation (FR-161, TC-107).
/// </summary>
/// <remarks>
/// <para>
/// The claim matters because it changes the response. A confirmed crawler is answered with an Open
/// Graph document instead of a redirect (ADR-009) and its click is flagged <c>is_bot</c>, which keeps
/// it out of every campaign report (FR-205). Trusting the user agent alone would therefore hand anyone
/// a free switch for suppressing a competitor's numbers, or for inflating their own by pretending not
/// to be a bot; TC-107 is that scenario written as a test.
/// </para>
/// <para>
/// The protocol is the one Google, Bing and Apple document. Resolve the address to a host name
/// (PTR), require that name to sit under a suffix the operator of the crawler owns, and then resolve
/// that name back to addresses and require the original address to be among them. The second half is
/// what makes it sound: a PTR record is published by whoever controls the address block, so without
/// the forward confirmation anyone could point their own reverse DNS at
/// <c>crawl-66-249-66-1.googlebot.com</c>.
/// </para>
/// <para>
/// Three properties keep two DNS round trips off the critical path. Only a request whose user agent
/// already claims to be a crawler can reach here at all; the verdict is cached per address and
/// crawler for <c>Dle:Edge:BotDetection:CacheTtlMinutes</c>; and each attempt runs under a hard
/// millisecond budget, because an unbounded lookup would turn a slow resolver into a slow redirect
/// (SHARED-KERNEL §17.8).
/// </para>
/// <para>
/// Crawlers that publish no reverse DNS convention — the social preview fetchers — are reported as
/// genuine rather than as forgeries. That is the bounded acceptance
/// <see cref="CrawlerCatalog"/> documents: confirming them would need their published address ranges,
/// downloading those would be the outbound third-party call NFR-14 forbids, and the worst a forged
/// <c>facebookexternalhit</c> obtains is the Open Graph document the link already publishes to anyone
/// it is shared with. Marking them unverified instead would break TC-106, which requires exactly that
/// document.
/// </para>
/// </remarks>
public sealed class BotVerifier : IBotVerifier
{
    /// <summary>
    /// How long a failed verification is remembered, as a fraction of the configured TTL.
    /// </summary>
    /// <remarks>
    /// A negative verdict is cached for a fifth of the positive one. A resolver outage otherwise pins
    /// every genuine crawler into the "spoofed" bucket for a full hour, which would silently empty
    /// every link preview on the internet for that long.
    /// </remarks>
    private const int NegativeTtlDivisor = 5;

    private readonly BotDetectionOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BotVerifier> _logger;
    private readonly ConcurrentDictionary<Key, Verdict> _cache = new();

    /// <summary>Creates the verifier.</summary>
    /// <param name="options">Bot detection options.</param>
    /// <param name="timeProvider">Clock used for cache expiry (SHARED-KERNEL §17.2).</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public BotVerifier(
        IOptions<BotDetectionOptions> options,
        TimeProvider timeProvider,
        ILogger<BotVerifier> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<bool> IsGenuineAsync(string crawlerName, IPAddress? address, CancellationToken ct)
    {
        // Not a crawler this build knows about. Nothing to confirm, so nothing is confirmed.
        if (!CrawlerCatalog.TryGetVerificationSuffixes(crawlerName, out string[] suffixes))
        {
            return false;
        }

        // Recognised but not confirmable by DNS: see the remarks on this type.
        if (suffixes.Length == 0)
        {
            return true;
        }

        if (!_options.ReverseDnsVerify)
        {
            return true;
        }

        if (address is null)
        {
            return false;
        }

        var key = new Key(crawlerName, address.ToString());
        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (_cache.TryGetValue(key, out Verdict cached) && cached.ExpiresAt > now)
        {
            return cached.IsGenuine;
        }

        bool genuine = await ConfirmAsync(crawlerName, address, suffixes, ct);

        Store(key, genuine, now);

        return genuine;
    }

    /// <summary>
    /// Runs the reverse lookup and the forward confirmation under the configured time budget.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A DNS failure of any kind must not fail the request. Every failure is logged and the branch " +
                        "returns false, which is the deny-by-default outcome SHARED-KERNEL §17.9 requires.")]
    private async ValueTask<bool> ConfirmAsync(
        string crawlerName,
        IPAddress address,
        string[] suffixes,
        CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(_options.VerificationTimeoutMs),
            _timeProvider);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        try
        {
            IPHostEntry reverse = await Dns.GetHostEntryAsync(address.ToString(), budget.Token);

            string? hostName = reverse.HostName;

            if (string.IsNullOrEmpty(hostName) || !MatchesSuffix(hostName, suffixes))
            {
                return false;
            }

            IPAddress[] forward = await Dns.GetHostAddressesAsync(hostName, budget.Token);

            foreach (IPAddress candidate in forward)
            {
                if (candidate.Equals(address))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception exception)
        {
            EdgeLog.BotVerificationFailed(_logger, crawlerName, exception);
            return false;
        }
    }

    /// <summary>
    /// Whether a reverse-resolved host name sits under one of the crawler operator's suffixes.
    /// </summary>
    /// <remarks>
    /// The comparison is anchored at a label boundary, which the leading dot in every catalogue entry
    /// supplies: without it <c>evil-googlebot.com</c> and <c>notgoogle.com</c> would both pass. The
    /// trailing dot of a fully qualified name is stripped first, because a resolver may or may not
    /// return it and a suffix never carries one.
    /// </remarks>
    private static bool MatchesSuffix(string hostName, string[] suffixes)
    {
        ReadOnlySpan<char> name = hostName.AsSpan().TrimEnd('.');

        foreach (string suffix in suffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Records a verdict, keeping the cache bounded.</summary>
    /// <remarks>
    /// The key space is attacker controlled — a client picks its own source address within whatever
    /// range it has — so the dictionary is capacity bounded. The reset is generational for the same
    /// reason it is in <see cref="ClientClassifier"/>: least-recently-used bookkeeping would cost more
    /// than the two DNS lookups it saves, and a flood should degrade to no caching rather than to
    /// unbounded memory.
    /// </remarks>
    private void Store(Key key, bool genuine, DateTimeOffset now)
    {
        int minutes = genuine
            ? _options.CacheTtlMinutes
            : Math.Max(1, _options.CacheTtlMinutes / NegativeTtlDivisor);

        if (_cache.Count >= _options.CacheCapacity)
        {
            _cache.Clear();
        }

        _cache[key] = new Verdict(genuine, now.AddMinutes(minutes));
    }

    /// <summary>Cache key: one crawler claim from one address.</summary>
    private readonly record struct Key(string CrawlerName, string Address);

    /// <summary>A cached verification result and the instant it stops being trusted.</summary>
    private readonly record struct Verdict(bool IsGenuine, DateTimeOffset ExpiresAt);
}
