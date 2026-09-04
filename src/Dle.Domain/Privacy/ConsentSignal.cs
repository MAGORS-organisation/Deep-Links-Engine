namespace Dle.Domain.Privacy;

/// <summary>
/// Consent as reported by the SDK or by a web consent management platform (specification §E.6.2).
/// The absence of a signal is never read as consent.
/// </summary>
public sealed record ConsentSignal
{
    /// <summary>The user agreed to analytics processing.</summary>
    public bool Analytics { get; init; }

    /// <summary>
    /// The user agreed to attribution, that is to persistent identifiers and cross session linking.
    /// This is the flag that unlocks <see cref="ConsentMode.Full"/>.
    /// </summary>
    public bool Attribution { get; init; }

    /// <summary>
    /// When the consent was captured, in UTC. Kept so that the decision stays auditable, which
    /// article 7(1) GDPR requires.
    /// </summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>Where the signal came from: <c>"sdk"</c>, <c>"cmp"</c>, <c>"tcf"</c> or <c>"header"</c>.</summary>
    public string? Source { get; init; }
}
