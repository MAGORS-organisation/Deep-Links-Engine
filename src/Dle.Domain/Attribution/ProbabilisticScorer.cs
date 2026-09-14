using System.Globalization;
using Dle.Domain.Primitives;

namespace Dle.Domain.Attribution;

/// <summary>
/// Scores a probabilistic match between the signals reported by a fresh installation and those
/// recorded with an earlier click (§A.2.5, strategy S4 of ADR-008).
/// </summary>
/// <remarks>
/// <para>
/// The published numbers are unflattering and this implementation is built around them:
/// independent analyses report roughly 98 % accuracy inside a ten minute window — which covers
/// only about 54 % of real attributions — about 81 % after seven days, and something close to a
/// coin flip beyond 24 hours. A fingerprint match older than a day is more likely wrong than
/// right. That is why the confidence decays linearly to zero across the window, why the default
/// window is 60 minutes rather than the seven days the commercial vendors use, and why the result
/// is never reported as a certainty (FR-186).
/// </para>
/// <para>
/// A signal that is absent on either side contributes nothing and its weight is <em>not</em>
/// redistributed over the remaining signals. Redistribution would let a device that reports a
/// single signal reach the same confidence as one that reports all five, which is exactly the
/// laundering this product refuses to do.
/// </para>
/// <para>
/// The caller compares the result against the configured minimum confidence and degrades to
/// <see cref="MatchType.None"/> when it is lower. Outside the window the score is exactly zero,
/// so a late match is never reported as a weak one (TC-147).
/// </para>
/// </remarks>
public static class ProbabilisticScorer
{
    /// <summary>Weight of an agreeing truncated network prefix. The strongest single signal.</summary>
    public const decimal IpPrefixWeight = 0.45m;

    /// <summary>Weight of an agreeing operating system version.</summary>
    public const decimal OsVersionWeight = 0.20m;

    /// <summary>Weight of an agreeing primary language subtag.</summary>
    public const decimal LanguageWeight = 0.15m;

    /// <summary>Weight of an agreeing time zone offset.</summary>
    public const decimal TimezoneWeight = 0.10m;

    /// <summary>Weight of agreeing screen geometry.</summary>
    public const decimal ScreenWeight = 0.10m;

    /// <summary>
    /// Scores one candidate click against the signals of an installation.
    /// </summary>
    /// <param name="candidate">Signals reported by the installation that is asking to be matched.</param>
    /// <param name="click">Signals recorded when the click happened.</param>
    /// <param name="elapsed">Time between the click and the resolve request. A negative value is
    /// treated as zero, since a click cannot happen after the install it produced.</param>
    /// <param name="window">Configured attribution window. Must be positive; a non positive
    /// window disables probabilistic matching and yields zero.</param>
    /// <returns>Confidence in the range 0.00 to 1.00, rounded to two decimal places. Exactly zero
    /// when nothing agreed, when the window has closed, or when <paramref name="elapsed"/> is at
    /// least <paramref name="window"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidate"/> or
    /// <paramref name="click"/> is <see langword="null"/>.</exception>
    public static decimal Score(DeviceSignals candidate, DeviceSignals click, TimeSpan elapsed, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(click);

        if (window <= TimeSpan.Zero || elapsed >= window)
        {
            return 0m;
        }

        decimal score = 0m;

        if (TextMatches(candidate.IpPrefix, click.IpPrefix))
        {
            score += IpPrefixWeight;
        }

        if (VersionMatches(candidate.OsVersion, click.OsVersion))
        {
            score += OsVersionWeight;
        }

        if (LanguageMatches(candidate.Language, click.Language))
        {
            score += LanguageWeight;
        }

        if (candidate.TimezoneOffsetMinutes is { } candidateOffset &&
            click.TimezoneOffsetMinutes is { } clickOffset &&
            candidateOffset == clickOffset)
        {
            score += TimezoneWeight;
        }

        if (TextMatches(candidate.Screen, click.Screen))
        {
            score += ScreenWeight;
        }

        if (score <= 0m)
        {
            return 0m;
        }

        long elapsedTicks = elapsed.Ticks > 0 ? elapsed.Ticks : 0;
        decimal decay = 1m - ((decimal)elapsedTicks / window.Ticks);

        if (decay <= 0m)
        {
            return 0m;
        }

        decimal confidence = score * decay;

        if (confidence < 0m)
        {
            confidence = 0m;
        }
        else if (confidence > 1m)
        {
            confidence = 1m;
        }

        return Math.Round(confidence, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Compares two opaque signal values. Missing on either side means no contribution.
    /// </summary>
    private static bool TextMatches(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Compares operating system versions with the shared version semantics, so that
    /// <c>17.4</c> and <c>17.4.0</c> are recognised as the same release.
    /// </summary>
    private static bool VersionMatches(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return VersionComparer.Compare(left, right) == 0;
    }

    /// <summary>
    /// Compares only the primary language subtag, because the click side stores the primary
    /// subtag while an SDK usually reports the full locale, for example <c>sk</c> and
    /// <c>sk-SK</c>.
    /// </summary>
    private static bool LanguageMatches(string? left, string? right)
    {
        string leftTag = PrimarySubtag(left);
        string rightTag = PrimarySubtag(right);

        return leftTag.Length != 0 && string.Equals(leftTag, rightTag, StringComparison.Ordinal);
    }

    private static string PrimarySubtag(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return string.Empty;
        }

        ReadOnlySpan<char> span = language.AsSpan().Trim();
        int separator = span.IndexOfAny('-', '_');

        if (separator >= 0)
        {
            span = span[..separator];
        }

        return span.IsEmpty ? string.Empty : span.ToString().ToLower(CultureInfo.InvariantCulture);
    }
}
