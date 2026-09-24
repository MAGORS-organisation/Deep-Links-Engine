namespace Dle.Domain.Abuse;

/// <summary>
/// Result of one safety check on a target URL (§E.3). Verdicts from different sources are
/// combined by keeping the most severe <see cref="Level"/>.
/// </summary>
/// <remarks>
/// Both the level and the source are recorded because they answer different questions: the level
/// decides whether the link may be stored, the source and reason explain the refusal to the
/// operator who has to defend it, and to the customer whose link was rejected.
/// </remarks>
public sealed record UrlSafetyVerdict
{
    /// <summary>How safe the target is judged to be.</summary>
    public required UrlSafetyLevel Level { get; init; }

    /// <summary>Which check produced the verdict: <c>syntax</c>, <c>private_ip</c>,
    /// <c>urlhaus</c>, <c>blocklist</c> or <c>manual</c>.</summary>
    public required string Source { get; init; }

    /// <summary>Human readable explanation, present on every refusal.</summary>
    public string? Reason { get; init; }

    /// <summary>When the check ran, in UTC. Used to decide when a target needs re-checking,
    /// because targets are routinely changed after a link is approved (§E.3, point 5).</summary>
    public DateTimeOffset? CheckedAt { get; init; }

    /// <summary>Creates a passing verdict.</summary>
    /// <param name="source">The check that passed.</param>
    /// <returns>A verdict at <see cref="UrlSafetyLevel.Safe"/> with no reason.</returns>
    public static UrlSafetyVerdict Safe(string source) => new()
    {
        Level = UrlSafetyLevel.Safe,
        Source = source,
    };

    /// <summary>Creates a refusing verdict.</summary>
    /// <param name="level">Severity of the refusal.</param>
    /// <param name="source">The check that refused.</param>
    /// <param name="reason">Why it refused. Always filled in; a refusal without a reason cannot
    /// be reviewed.</param>
    /// <returns>The refusing verdict.</returns>
    public static UrlSafetyVerdict Reject(UrlSafetyLevel level, string source, string reason) => new()
    {
        Level = level,
        Source = source,
        Reason = reason,
    };
}
