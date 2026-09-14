using Dle.Domain.Privacy;
using Xunit;

namespace Dle.UnitTests.Privacy;

/// <summary>
/// SHARED-KERNEL section 2 truth table. Every row is a legal position, not a preference: ePrivacy
/// art. 5(3) and EDPB 2/2023 are what make the "Full without attribution consent" row degrade
/// rather than pass through.
/// </summary>
public sealed class ConsentGateTests
{
    // ---------------------------------------------------------------- row 1: Off

    [Fact]
    [Trait("TestCase", "TC-145")]
    public void Evaluate_TenantOff_DeniesEverything()
    {
        ConsentDecision decision = ConsentGate.Evaluate(ConsentMode.Off, null, Consented());

        Assert.Equal(ConsentMode.Off, decision.EffectiveMode);
        AssertDenied(decision);
        Assert.Equal(ConsentGate.ReasonTenantOff, decision.Reason);
    }

    [Fact]
    public void Evaluate_TenantOffWithWiderDomainOverride_StillDeniesEverything()
    {
        ConsentDecision decision = ConsentGate.Evaluate(ConsentMode.Off, ConsentMode.Full, Consented());

        Assert.Equal(ConsentMode.Off, decision.EffectiveMode);
        AssertDenied(decision);
    }

    // ---------------------------------------------------------------- row 2: AggregateOnly

    [Fact]
    [Trait("TestCase", "TC-145")]
    public void Evaluate_AggregateOnly_StoresHashedIpAndNothingElse()
    {
        ConsentDecision decision = ConsentGate.Evaluate(ConsentMode.AggregateOnly, null, Consented());

        Assert.Equal(ConsentMode.AggregateOnly, decision.EffectiveMode);
        Assert.True(decision.StoreIpHash);
        Assert.False(decision.StoreIpPrefix);
        Assert.False(decision.StoreDeviceSignals);
        Assert.False(decision.AllowClickIdLinking);
        Assert.False(decision.AllowProbabilisticMatch);
        Assert.Equal(ConsentGate.ReasonAggregateDefault, decision.Reason);
    }

    // ---------------------------------------------------------------- row 3: Full with consent

    [Fact]
    public void Evaluate_FullWithAttributionConsent_AllowsEverything()
    {
        ConsentDecision decision = ConsentGate.Evaluate(ConsentMode.Full, null, Consented());

        Assert.Equal(ConsentMode.Full, decision.EffectiveMode);
        Assert.True(decision.StoreIpHash);
        Assert.True(decision.StoreIpPrefix);
        Assert.True(decision.StoreDeviceSignals);
        Assert.True(decision.AllowClickIdLinking);
        Assert.True(decision.AllowProbabilisticMatch);
        Assert.Equal(ConsentGate.ReasonFullWithConsent, decision.Reason);
    }

    // ---------------------------------------------------------------- row 4: Full, no attribution consent

    [Fact]
    public void Evaluate_FullWithAnalyticsButNotAttributionConsent_DegradesToHashedIpOnly()
    {
        ConsentDecision decision = ConsentGate.Evaluate(
            ConsentMode.Full,
            null,
            new ConsentSignal { Analytics = true, Attribution = false });

        Assert.Equal(ConsentMode.Full, decision.EffectiveMode);
        Assert.True(decision.StoreIpHash);
        Assert.False(decision.StoreIpPrefix);
        Assert.False(decision.StoreDeviceSignals);
        Assert.False(decision.AllowClickIdLinking);
        Assert.False(decision.AllowProbabilisticMatch);
        Assert.Equal(ConsentGate.ReasonNoAttributionConsent, decision.Reason);
    }

    [Fact]
    [Trait("TestCase", "TC-146")]
    public void Evaluate_FullWithoutAnySignal_DoesNotAllowDeviceSignalProcessing()
    {
        ConsentDecision decision = ConsentGate.Evaluate(ConsentMode.Full, null, signal: null);

        Assert.Equal(ConsentMode.Full, decision.EffectiveMode);
        Assert.True(decision.StoreIpHash);
        Assert.False(decision.StoreIpPrefix);
        Assert.False(decision.StoreDeviceSignals);
        Assert.False(decision.AllowClickIdLinking);
        Assert.False(decision.AllowProbabilisticMatch);
        Assert.Equal(ConsentGate.ReasonNoAttributionConsent, decision.Reason);
    }

    [Fact]
    [Trait("TestCase", "TC-146")]
    public void Evaluate_FullWithAttributionDenied_ForbidsProbabilisticMatching()
    {
        ConsentDecision decision = ConsentGate.Evaluate(
            ConsentMode.Full,
            null,
            new ConsentSignal { Attribution = false, Source = "cmp" });

        Assert.False(decision.AllowProbabilisticMatch);
        Assert.False(decision.StoreDeviceSignals);
    }

    // ---------------------------------------------------------------- domain override: tighten only

    [Theory]
    [InlineData(ConsentMode.Full, ConsentMode.Off, ConsentMode.Off)]
    [InlineData(ConsentMode.Full, ConsentMode.AggregateOnly, ConsentMode.AggregateOnly)]
    [InlineData(ConsentMode.Full, ConsentMode.Full, ConsentMode.Full)]
    [InlineData(ConsentMode.AggregateOnly, ConsentMode.Off, ConsentMode.Off)]
    [InlineData(ConsentMode.AggregateOnly, ConsentMode.AggregateOnly, ConsentMode.AggregateOnly)]
    [InlineData(ConsentMode.AggregateOnly, ConsentMode.Full, ConsentMode.AggregateOnly)]
    [InlineData(ConsentMode.Off, ConsentMode.Full, ConsentMode.Off)]
    public void Evaluate_DomainOverride_CanOnlyTighten(
        ConsentMode tenant,
        ConsentMode domain,
        ConsentMode expected)
    {
        ConsentDecision decision = ConsentGate.Evaluate(tenant, domain, Consented());

        Assert.Equal(expected, decision.EffectiveMode);
    }

    [Fact]
    public void Evaluate_DomainOverrideWiderThanTenant_DoesNotLoosenPermissions()
    {
        ConsentDecision decision = ConsentGate.Evaluate(ConsentMode.AggregateOnly, ConsentMode.Full, Consented());

        Assert.Equal(ConsentMode.AggregateOnly, decision.EffectiveMode);
        Assert.False(decision.AllowProbabilisticMatch);
        Assert.False(decision.AllowClickIdLinking);
        Assert.False(decision.StoreIpPrefix);
    }

    [Fact]
    public void Evaluate_DomainOverrideOff_ReportsTheOverrideAsTheReason()
    {
        ConsentDecision decision = ConsentGate.Evaluate(ConsentMode.Full, ConsentMode.Off, Consented());

        AssertDenied(decision);
        Assert.Equal(ConsentGate.ReasonDomainOverride, decision.Reason);
    }

    [Fact]
    public void Evaluate_DomainOverrideTightensFullToAggregate_ReportsTheOverrideAsTheReason()
    {
        ConsentDecision decision = ConsentGate.Evaluate(ConsentMode.Full, ConsentMode.AggregateOnly, Consented());

        Assert.Equal(ConsentGate.ReasonDomainOverride, decision.Reason);
    }

    [Fact]
    public void Evaluate_NoDomainOverride_LeavesTheTenantModeUntouched()
    {
        ConsentDecision decision = ConsentGate.Evaluate(ConsentMode.Full, domainOverride: null, Consented());

        Assert.Equal(ConsentMode.Full, decision.EffectiveMode);
    }

    // ---------------------------------------------------------------- defensive

    [Fact]
    public void Evaluate_UndefinedTenantModeValue_IsTreatedAsOff()
    {
        ConsentDecision decision = ConsentGate.Evaluate((ConsentMode)99, null, Consented());

        Assert.Equal(ConsentMode.Off, decision.EffectiveMode);
        AssertDenied(decision);
    }

    [Fact]
    public void Evaluate_UndefinedDomainOverrideValue_IsTreatedAsOff()
    {
        ConsentDecision decision = ConsentGate.Evaluate(ConsentMode.Full, (ConsentMode)99, Consented());

        Assert.Equal(ConsentMode.Off, decision.EffectiveMode);
        AssertDenied(decision);
    }

    [Fact]
    public void Denied_ProducesAnAllFalseDecisionCarryingTheReason()
    {
        ConsentDecision decision = ConsentDecision.Denied("kill_switch");

        Assert.Equal(ConsentMode.Off, decision.EffectiveMode);
        AssertDenied(decision);
        Assert.Equal("kill_switch", decision.Reason);
    }

    private static ConsentSignal Consented() =>
        new() { Analytics = true, Attribution = true, Source = "sdk" };

    private static void AssertDenied(ConsentDecision decision)
    {
        Assert.False(decision.StoreIpHash);
        Assert.False(decision.StoreIpPrefix);
        Assert.False(decision.StoreDeviceSignals);
        Assert.False(decision.AllowClickIdLinking);
        Assert.False(decision.AllowProbabilisticMatch);
    }
}
