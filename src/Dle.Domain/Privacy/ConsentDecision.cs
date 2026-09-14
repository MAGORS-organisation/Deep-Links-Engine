namespace Dle.Domain.Privacy;

/// <summary>
/// The outcome of the consent gate. It is an <em>input</em> to the decision pipeline and not a
/// post hoc filter (specification §B.1/5): a signal that must not be processed is never collected,
/// rather than collected and deleted later (TC-145, TC-146).
/// </summary>
public sealed record ConsentDecision
{
    /// <summary>The mode that actually applies, after the domain override has been intersected with the tenant mode.</summary>
    public required ConsentMode EffectiveMode { get; init; }

    /// <summary>The salted HMAC of the remote address may be stored.</summary>
    public required bool StoreIpHash { get; init; }

    /// <summary>The truncated network prefix (/24 for IPv4, /48 for IPv6) may be stored.</summary>
    public required bool StoreIpPrefix { get; init; }

    /// <summary>Device signals (language, screen, timezone, model) may be stored.</summary>
    public required bool StoreDeviceSignals { get; init; }

    /// <summary>A click identifier may be linked to an install, which is what makes deferred deep linking work.</summary>
    public required bool AllowClickIdLinking { get; init; }

    /// <summary>The probabilistic matcher may run.</summary>
    public required bool AllowProbabilisticMatch { get; init; }

    /// <summary>
    /// Stable snake_case code explaining the decision, one of the <c>Reason*</c> constants on
    /// <see cref="ConsentGate"/>. It is written to the audit trail, so the values never change.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Builds the decision that permits nothing: mode <see cref="ConsentMode.Off"/> and every flag
    /// cleared.
    /// </summary>
    /// <param name="reason">The stable snake_case reason code.</param>
    /// <returns>A decision under which no identifier of any kind may be stored.</returns>
    public static ConsentDecision Denied(string reason) => new()
    {
        EffectiveMode = ConsentMode.Off,
        StoreIpHash = false,
        StoreIpPrefix = false,
        StoreDeviceSignals = false,
        AllowClickIdLinking = false,
        AllowProbabilisticMatch = false,
        Reason = reason,
    };
}
