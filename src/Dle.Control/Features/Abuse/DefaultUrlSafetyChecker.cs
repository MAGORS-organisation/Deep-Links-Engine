using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

using Dle.Domain.Abuse;
using Dle.Domain.Ports;

using Microsoft.Extensions.Logging;

namespace Dle.Control.Features.Abuse;

/// <summary>
/// The default <see cref="IUrlSafetyChecker"/>: syntax, then the resolved address, then reputation
/// (§E.3 steps 1 to 3, T-01, T-02).
/// </summary>
/// <remarks>
/// <para>
/// The order is the point. Syntax is free and rejects the whole <c>javascript:</c> family without
/// touching the network. The address check is what stops SSRF, and it is done on the addresses the
/// name actually resolves to rather than on the name, because <c>evil.example</c> resolving to
/// <c>169.254.169.254</c> is a hostname that passes any name based check. Reputation is last
/// because it is the only step that involves a third party and the only one that can be slow.
/// </para>
/// <para>
/// Every branch ends in an explicit verdict and the method has no path that falls through to
/// approval (SHARED-KERNEL §17.9). A target is approved only when the syntax passed, every
/// resolved address was public, and no consulted source objected.
/// </para>
/// </remarks>
public sealed class DefaultUrlSafetyChecker : IUrlSafetyChecker
{
    /// <summary>Source name used for verdicts about the resolved address.</summary>
    private const string AddressSource = "private_ip";

    private readonly IEnumerable<IUrlReputationProvider> _providers;
    private readonly AbuseMetrics _metrics;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DefaultUrlSafetyChecker> _logger;

    /// <summary>Creates the checker.</summary>
    /// <param name="providers">Reputation sources, consulted in registration order.</param>
    /// <param name="metrics">Instruments.</param>
    /// <param name="timeProvider">Clock used to stamp verdicts.</param>
    /// <param name="logger">Logger.</param>
    public DefaultUrlSafetyChecker(
        IEnumerable<IUrlReputationProvider> providers,
        AbuseMetrics metrics,
        TimeProvider timeProvider,
        ILogger<DefaultUrlSafetyChecker> logger)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _providers = providers;
        _metrics = metrics;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<UrlSafetyVerdict> CheckAsync(string url, CancellationToken ct)
    {
        UrlSafetyVerdict syntax = TargetUrlPolicy.ValidateSyntax(url);

        if (syntax.Level != UrlSafetyLevel.Safe)
        {
            _metrics.Blocked(syntax.Source, LevelTag(syntax.Level));
            return Stamp(syntax);
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? target))
        {
            // Unreachable through ValidateSyntax, which parses the same string; kept because a
            // decision branch that cannot prove its input must still deny rather than continue.
            return Stamp(UrlSafetyVerdict.Reject(
                UrlSafetyLevel.Blocked,
                "syntax",
                "The target URL is not an absolute URL."));
        }

        UrlSafetyVerdict? address = await CheckAddressAsync(target, ct);

        if (address is not null)
        {
            _metrics.Blocked(address.Source, LevelTag(address.Level));
            return Stamp(address);
        }

        foreach (IUrlReputationProvider provider in _providers)
        {
            if (!provider.IsEnabled)
            {
                continue;
            }

            UrlSafetyVerdict? verdict = await provider.CheckAsync(target, ct);

            if (verdict is null || verdict.Level == UrlSafetyLevel.Safe)
            {
                continue;
            }

            _metrics.Blocked(verdict.Source, LevelTag(verdict.Level));
            return Stamp(verdict);
        }

        return Stamp(UrlSafetyVerdict.Safe("syntax"));
    }

    /// <summary>
    /// Resolves the host and judges every address it answers with.
    /// </summary>
    /// <param name="target">The target URL.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A refusal, or <see langword="null"/> when every resolved address is public.</returns>
    /// <remarks>
    /// Every address has to be public, not merely the first: a name that answers with one routable
    /// address and one link-local address is a rebinding attack with the work already done for it.
    /// A name that does not resolve at all is refused as well — a short link to a host that does
    /// not exist is either a typo or a name the abuser intends to register later.
    /// </remarks>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification =
            "Name resolution is the step most likely to throw something unforeseen, and the only "
            + "safe answer to 'we could not establish where this points' is refusal. The exception "
            + "is logged and turned into an explicit deny, never into an approval (§17.9).")]
    private async Task<UrlSafetyVerdict?> CheckAddressAsync(Uri target, CancellationToken ct)
    {
        if (TargetUrlPolicy.IsForbiddenHost(target.Host))
        {
            return UrlSafetyVerdict.Reject(
                UrlSafetyLevel.Blocked,
                AddressSource,
                "The target host is an internal name and never points at a public target.");
        }

        try
        {
            IPAddress[] addresses = IPAddress.TryParse(target.Host, out IPAddress? literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(target.Host, ct);

            if (addresses.Length == 0)
            {
                return UrlSafetyVerdict.Reject(
                    UrlSafetyLevel.Blocked,
                    AddressSource,
                    "The target host does not resolve to any address.");
            }

            foreach (IPAddress candidate in addresses)
            {
                if (TargetUrlPolicy.IsForbiddenAddress(candidate))
                {
                    return UrlSafetyVerdict.Reject(
                        UrlSafetyLevel.Blocked,
                        AddressSource,
                        "The target host resolves to an address that is not a permitted target.");
                }
            }

            return null;
        }
        catch (SocketException)
        {
            // A name that does not resolve is not an infrastructure failure worth an error log; it
            // is an ordinary rejection an operator sees in the API response.
            return UrlSafetyVerdict.Reject(
                UrlSafetyLevel.Blocked,
                AddressSource,
                "The target host could not be resolved.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Resolving a target host failed unexpectedly; the target was refused.");

            return UrlSafetyVerdict.Reject(
                UrlSafetyLevel.Blocked,
                AddressSource,
                "The target host could not be checked and was refused.");
        }
    }

    /// <summary>Records when a verdict was reached.</summary>
    /// <param name="verdict">The verdict.</param>
    /// <returns>The verdict with its instant filled in.</returns>
    private UrlSafetyVerdict Stamp(UrlSafetyVerdict verdict) =>
        verdict.CheckedAt is null
            ? verdict with { CheckedAt = _timeProvider.GetUtcNow() }
            : verdict;

    /// <summary>Renders a level for a metric tag.</summary>
    /// <param name="level">The verdict level.</param>
    /// <returns>The lowercase name.</returns>
    internal static string LevelTag(UrlSafetyLevel level) => level switch
    {
        UrlSafetyLevel.Safe => "safe",
        UrlSafetyLevel.Suspicious => "suspicious",
        UrlSafetyLevel.Malicious => "malicious",
        UrlSafetyLevel.Blocked => "blocked",
        _ => "unknown",
    };

    /// <summary>Formats a verdict for an operator facing message.</summary>
    /// <param name="verdict">The verdict.</param>
    /// <returns>A single line naming the source and the reason.</returns>
    internal static string Describe(UrlSafetyVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{LevelTag(verdict.Level)} ({verdict.Source}): {verdict.Reason ?? "no reason given"}");
    }
}
