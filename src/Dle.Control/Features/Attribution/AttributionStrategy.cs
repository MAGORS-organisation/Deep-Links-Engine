using Dle.Domain.Attribution;

namespace Dle.Control.Features.Attribution;

/// <summary>
/// The four deferred matching strategies of ADR-008, in the order the default configuration tries
/// them.
/// </summary>
/// <remarks>
/// This is not the same thing as <see cref="Dle.Domain.Attribution.MatchType"/>. A match type says
/// what produced an attribution and is a persisted, public value; a strategy says what the engine
/// is about to attempt. <see cref="Dle.Domain.Attribution.MatchType.DirectOpen"/> has no strategy
/// here because it is not deferred at all: it arrives as a <c>link_open</c> event on
/// <c>POST /v1/events</c> (§B.6.4, FR-223).
/// </remarks>
public enum AttributionStrategy
{
    /// <summary>The configured name was not recognised.</summary>
    Unknown = 0,

    /// <summary>S1 — the Android Play Install Referrer carries the click identifier. Deterministic.</summary>
    InstallReferrer = 1,

    /// <summary>S2 — an account hash reported after sign-in matches one recorded with a click. Deterministic.</summary>
    Login = 2,

    /// <summary>S3 — the user typed the code shown on the interstitial page. Deterministic.</summary>
    ClaimCode = 3,

    /// <summary>S4 — device signals resemble a recent click. Never deterministic, opt-in, consent gated.</summary>
    Probabilistic = 4,
}

/// <summary>
/// Configuration names of the strategies, as they appear in <c>Dle:Attribution:Strategies</c>.
/// </summary>
/// <remarks>
/// The names deliberately equal the corresponding <see cref="Dle.Domain.Attribution.MatchTypeNames"/>
/// values, so an operator reading a configuration file and an analyst reading the
/// <c>attributions.match_type</c> column are looking at the same vocabulary.
/// </remarks>
public static class AttributionStrategyNames
{
    /// <summary>Configuration name of <see cref="AttributionStrategy.InstallReferrer"/>.</summary>
    public const string InstallReferrer = MatchTypeNames.InstallReferrer;

    /// <summary>Configuration name of <see cref="AttributionStrategy.Login"/>.</summary>
    public const string Login = MatchTypeNames.Login;

    /// <summary>Configuration name of <see cref="AttributionStrategy.ClaimCode"/>.</summary>
    public const string ClaimCode = MatchTypeNames.ClaimCode;

    /// <summary>Configuration name of <see cref="AttributionStrategy.Probabilistic"/>.</summary>
    public const string Probabilistic = MatchTypeNames.Probabilistic;

    /// <summary>Parses a configured strategy name.</summary>
    /// <param name="value">The name as configured. Matching is case insensitive and surrounding
    /// whitespace is ignored.</param>
    /// <returns>The strategy, or <see cref="AttributionStrategy.Unknown"/> for anything else. The
    /// parser fails closed: an unrecognised name never silently becomes a strategy that runs.</returns>
    public static AttributionStrategy Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return AttributionStrategy.Unknown;
        }

        string trimmed = value.Trim();

        if (string.Equals(trimmed, InstallReferrer, StringComparison.OrdinalIgnoreCase))
        {
            return AttributionStrategy.InstallReferrer;
        }

        if (string.Equals(trimmed, Login, StringComparison.OrdinalIgnoreCase))
        {
            return AttributionStrategy.Login;
        }

        if (string.Equals(trimmed, ClaimCode, StringComparison.OrdinalIgnoreCase))
        {
            return AttributionStrategy.ClaimCode;
        }

        if (string.Equals(trimmed, Probabilistic, StringComparison.OrdinalIgnoreCase))
        {
            return AttributionStrategy.Probabilistic;
        }

        return AttributionStrategy.Unknown;
    }
}
