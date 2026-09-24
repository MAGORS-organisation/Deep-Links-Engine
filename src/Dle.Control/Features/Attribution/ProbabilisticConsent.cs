using Dle.Domain.Attribution;
using Dle.Domain.Contracts;
using Dle.Domain.Privacy;

namespace Dle.Control.Features.Attribution;

/// <summary>
/// The capability that has to be held before device signals may be looked at, let alone stored
/// (TC-145, TC-146, §E.6.2).
/// </summary>
/// <remarks>
/// <para>
/// Consent gating here is structural, not procedural. <see cref="Read"/> is the only place in this
/// module that turns a <see cref="DeviceSignalsDto"/> into a <see cref="DeviceSignals"/>, it is an
/// instance method, and the only way to obtain an instance is <see cref="TryGrant"/>, which returns
/// <see langword="null"/> unless the module is enabled, the strategy is in the configured order and
/// the consent gate allows probabilistic matching. With the module off or with
/// <c>consent.attribution=false</c> the resolve path therefore holds no object that could read the
/// signals, and the payload is dropped with the request body — it is never mapped, never scored,
/// never written to <c>attributions.evidence</c> and never persisted anywhere else.
/// </para>
/// <para>
/// This is the difference §E.6.2 insists on: "without documented consent the signals are not
/// processed and not written" — not "written and deleted later". A boolean checked before an insert
/// would satisfy the letter of the test and none of its intent, because the next person to add a
/// code path would simply forget the check. A capability cannot be forgotten; the code that would
/// process the signals does not compile without it.
/// </para>
/// </remarks>
public sealed class ProbabilisticConsent
{
    private ProbabilisticConsent(TimeSpan window, decimal minConfidence, string consentReason)
    {
        Window = window;
        MinConfidence = minConfidence;
        ConsentReason = consentReason;
    }

    /// <summary>Width of the matching window that was in force when the grant was made.</summary>
    public TimeSpan Window { get; }

    /// <summary>Lowest confidence that still counts as a match.</summary>
    public decimal MinConfidence { get; }

    /// <summary>The <see cref="ConsentGate"/> reason code that justified the grant. Written to the
    /// evidence document so the legal basis of the match stays auditable.</summary>
    public string ConsentReason { get; }

    /// <summary>
    /// Grants the capability, or refuses it.
    /// </summary>
    /// <param name="options">The attribution options.</param>
    /// <param name="order">The configured strategy order.</param>
    /// <param name="decision">The outcome of the consent gate for this request.</param>
    /// <returns>The capability, or <see langword="null"/> when probabilistic matching must not
    /// happen. Three independent conditions all have to hold, and each of them alone is a veto:
    /// the module is enabled, <c>probabilistic</c> is named in the configured order, and the
    /// consent gate returned <see cref="ConsentDecision.AllowProbabilisticMatch"/>.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static ProbabilisticConsent? TryGrant(
        AttributionOptions options,
        IReadOnlyList<AttributionStrategy> order,
        ConsentDecision decision)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(decision);

        if (!options.Probabilistic.Enabled)
        {
            return null;
        }

        if (!order.Contains(AttributionStrategy.Probabilistic))
        {
            return null;
        }

        if (!decision.AllowProbabilisticMatch)
        {
            return null;
        }

        return new ProbabilisticConsent(
            options.Probabilistic.Window,
            options.Probabilistic.MinConfidence,
            decision.Reason);
    }

    /// <summary>
    /// Reads the device signals of a resolve request. The only conversion from the wire shape to
    /// the domain shape in this module.
    /// </summary>
    /// <param name="signals">The signals as received, or <see langword="null"/> when the SDK sent
    /// none.</param>
    /// <param name="ipPrefix">Truncated network prefix of the caller, or <see langword="null"/>.
    /// It is derived from the connection rather than taken from the payload, because a client that
    /// could name its own prefix could name someone else's click.</param>
    /// <param name="osVersion">Operating system version reported alongside the request.</param>
    /// <returns>The signals, or <see langword="null"/> when nothing usable was supplied — a request
    /// that offers no signal at all cannot be matched probabilistically and must not spend a query
    /// finding that out.</returns>
    public DeviceSignals? Read(DeviceSignalsDto? signals, string? ipPrefix, string? osVersion)
    {
        string? language = Trim(signals?.Language);
        string? screen = Trim(signals?.Screen);
        string? model = Trim(signals?.DeviceModel);
        string? prefix = Trim(ipPrefix);
        string? version = Trim(osVersion);
        int? timezone = signals?.TzOffset is { } offset && offset is >= -840 and <= 840 ? offset : null;

        if (language is null && screen is null && model is null && prefix is null && version is null && timezone is null)
        {
            return null;
        }

        return new DeviceSignals
        {
            Language = language,
            Screen = screen,
            TimezoneOffsetMinutes = timezone,
            OsVersion = version,
            DeviceModel = model,
            IpPrefix = prefix,
        };
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
