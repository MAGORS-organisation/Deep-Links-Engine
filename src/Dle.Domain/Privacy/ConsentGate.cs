namespace Dle.Domain.Privacy;

/// <summary>
/// Turns a tenant configuration, an optional per domain override and an optional consent signal
/// into the single decision that the whole request pipeline obeys (specification §E.6.2).
/// </summary>
/// <remarks>
/// <para>
/// Two invariants hold for every possible input. First, the effective mode is the
/// <em>minimum</em> of the tenant mode and the domain override: a domain may tighten what the
/// tenant allows, never loosen it. Second, <see cref="ConsentMode.Full"/> only unlocks the
/// identifiers it describes when the request carries <see cref="ConsentSignal.Attribution"/>;
/// configuration alone is not a legal basis under ePrivacy article 5(3).
/// </para>
/// <para>The behaviour matrix, covered by TC-145 and TC-146:</para>
/// <list type="table">
///   <listheader>
///     <term>Effective mode</term>
///     <description>IpHash / IpPrefix / DeviceSignals / ClickIdLinking / Probabilistic</description>
///   </listheader>
///   <item><term><c>Off</c></term><description>no / no / no / no / no</description></item>
///   <item><term><c>AggregateOnly</c></term><description>yes / no / no / no / no</description></item>
///   <item><term><c>Full</c> with attribution consent</term><description>yes / yes / yes / yes / yes</description></item>
///   <item><term><c>Full</c> without it</term><description>yes / no / no / no / no</description></item>
/// </list>
/// </remarks>
public static class ConsentGate
{
    /// <summary>The tenant runs in <see cref="ConsentMode.Off"/>, so nothing may be stored.</summary>
    public const string ReasonTenantOff = "tenant_off";

    /// <summary>The link domain tightened the mode below what the tenant allows.</summary>
    public const string ReasonDomainOverride = "domain_override";

    /// <summary>The effective mode is <see cref="ConsentMode.Full"/> but the request carries no attribution consent.</summary>
    public const string ReasonNoAttributionConsent = "no_attribution_consent";

    /// <summary>The effective mode is <see cref="ConsentMode.Full"/> and attribution consent is present.</summary>
    public const string ReasonFullWithConsent = "full_with_consent";

    /// <summary>The effective mode is the default <see cref="ConsentMode.AggregateOnly"/>.</summary>
    public const string ReasonAggregateDefault = "aggregate_default";

    /// <summary>
    /// Evaluates the gate.
    /// </summary>
    /// <param name="tenantMode">The mode configured on the tenant (<c>Tenant.ConsentMode</c>).</param>
    /// <param name="domainOverride">
    /// The override configured on the link domain (<c>LinkDomain.ConsentModeOverride</c>), or
    /// <see langword="null"/> when the domain inherits the tenant mode. A value that is less
    /// restrictive than the tenant mode is ignored.
    /// </param>
    /// <param name="signal">
    /// The consent signal from the SDK or the consent management platform, or <see langword="null"/>
    /// when the request carries none. A missing signal is treated exactly like a refused one.
    /// </param>
    /// <returns>The decision that every later stage of the pipeline must honour.</returns>
    public static ConsentDecision Evaluate(
        ConsentMode tenantMode,
        ConsentMode? domainOverride,
        ConsentSignal? signal)
    {
        ConsentMode effective = Clamp(tenantMode);

        if (effective == ConsentMode.Off)
        {
            return ConsentDecision.Denied(ReasonTenantOff);
        }

        bool tightened = false;

        if (domainOverride is { } domainMode && Clamp(domainMode) < effective)
        {
            effective = Clamp(domainMode);
            tightened = true;
        }

        if (effective == ConsentMode.Off)
        {
            return ConsentDecision.Denied(ReasonDomainOverride);
        }

        if (effective == ConsentMode.Full && signal is { Attribution: true })
        {
            return new ConsentDecision
            {
                EffectiveMode = ConsentMode.Full,
                StoreIpHash = true,
                StoreIpPrefix = true,
                StoreDeviceSignals = true,
                AllowClickIdLinking = true,
                AllowProbabilisticMatch = true,
                Reason = ReasonFullWithConsent,
            };
        }

        // Either aggregate_only, or full without a documented attribution consent. Both degrade to
        // the same set of permissions: the hashed address and nothing else.
        string reason = effective == ConsentMode.Full
            ? ReasonNoAttributionConsent
            : tightened ? ReasonDomainOverride : ReasonAggregateDefault;

        return new ConsentDecision
        {
            EffectiveMode = effective,
            StoreIpHash = true,
            StoreIpPrefix = false,
            StoreDeviceSignals = false,
            AllowClickIdLinking = false,
            AllowProbabilisticMatch = false,
            Reason = reason,
        };
    }

    /// <summary>
    /// Maps any value that is not a declared member onto <see cref="ConsentMode.Off"/>. The gate
    /// fails closed: an unrecognised mode, for example a value read from a corrupted row, must never
    /// grant more than the most restrictive setting.
    /// </summary>
    private static ConsentMode Clamp(ConsentMode mode) => mode switch
    {
        ConsentMode.AggregateOnly => ConsentMode.AggregateOnly,
        ConsentMode.Full => ConsentMode.Full,
        _ => ConsentMode.Off,
    };
}
