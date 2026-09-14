using Dle.Domain.Links;
using Xunit;

namespace Dle.UnitTests.Links;

/// <summary>
/// SHARED-KERNEL section 5: the order of the checks in GetServeState is the contract, not an
/// implementation detail. A quarantined link that also expired must answer 410 Gone (TC-103), never
/// a redirect to the expired URL, because the abuse decision outranks the schedule.
/// </summary>
public sealed class LinkSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GetServeState_ActiveLinkWithoutSchedule_IsServable()
    {
        LinkSnapshot link = TestLinks.Snapshot();

        Assert.Equal(LinkServeState.Servable, link.GetServeState(Now));
        Assert.True(link.IsServable(Now));
    }

    [Fact]
    [Trait("TestCase", "TC-103")]
    public void GetServeState_QuarantinedLink_IsGone()
    {
        LinkSnapshot link = TestLinks.Snapshot(quarantinedAt: Now.AddDays(-1));

        Assert.Equal(LinkServeState.Gone, link.GetServeState(Now));
        Assert.False(link.IsServable(Now));
    }

    [Fact]
    [Trait("TestCase", "TC-103")]
    public void GetServeState_QuarantinedAndExpiredLink_IsGoneNotExpired()
    {
        LinkSnapshot link = TestLinks.Snapshot(
            quarantinedAt: Now.AddDays(-1),
            expiresAt: Now.AddDays(-2),
            expiredUrl: "https://expired.example.com/");

        Assert.Equal(LinkServeState.Gone, link.GetServeState(Now));
    }

    [Fact]
    [Trait("TestCase", "TC-103")]
    public void GetServeState_QuarantinedAndInactiveAndNotYetStarted_IsGone()
    {
        LinkSnapshot link = TestLinks.Snapshot(
            quarantinedAt: Now,
            isActive: false,
            startsAt: Now.AddDays(1),
            expiresAt: Now.AddDays(2));

        Assert.Equal(LinkServeState.Gone, link.GetServeState(Now));
    }

    [Fact]
    public void GetServeState_InactiveLink_IsNotFound()
    {
        LinkSnapshot link = TestLinks.Snapshot(isActive: false);

        Assert.Equal(LinkServeState.NotFound, link.GetServeState(Now));
    }

    [Fact]
    public void GetServeState_InactiveAndExpiredLink_IsNotFoundNotExpired()
    {
        LinkSnapshot link = TestLinks.Snapshot(isActive: false, expiresAt: Now.AddDays(-1));

        Assert.Equal(LinkServeState.NotFound, link.GetServeState(Now));
    }

    [Fact]
    public void GetServeState_LinkScheduledForTheFuture_IsNotYetActive()
    {
        LinkSnapshot link = TestLinks.Snapshot(startsAt: Now.AddSeconds(1));

        Assert.Equal(LinkServeState.NotYetActive, link.GetServeState(Now));
    }

    [Fact]
    public void GetServeState_AtTheStartInstant_IsAlreadyServable()
    {
        LinkSnapshot link = TestLinks.Snapshot(startsAt: Now);

        Assert.Equal(LinkServeState.Servable, link.GetServeState(Now));
    }

    [Fact]
    public void GetServeState_NotYetStartedAndAlreadyExpired_IsNotYetActive()
    {
        LinkSnapshot link = TestLinks.Snapshot(startsAt: Now.AddDays(1), expiresAt: Now.AddHours(-1));

        Assert.Equal(LinkServeState.NotYetActive, link.GetServeState(Now));
    }

    [Fact]
    [Trait("TestCase", "TC-104")]
    public void GetServeState_PastTheExpiryInstant_IsExpired()
    {
        LinkSnapshot link = TestLinks.Snapshot(
            expiresAt: Now.AddSeconds(-1),
            expiredUrl: "https://expired.example.com/");

        Assert.Equal(LinkServeState.Expired, link.GetServeState(Now));
    }

    [Fact]
    [Trait("TestCase", "TC-104")]
    public void GetServeState_AtTheExpiryInstant_IsAlreadyExpired()
    {
        LinkSnapshot link = TestLinks.Snapshot(expiresAt: Now);

        Assert.Equal(LinkServeState.Expired, link.GetServeState(Now));
    }

    [Fact]
    public void GetServeState_OneTickBeforeExpiry_IsStillServable()
    {
        LinkSnapshot link = TestLinks.Snapshot(expiresAt: Now.AddTicks(1));

        Assert.Equal(LinkServeState.Servable, link.GetServeState(Now));
    }

    [Fact]
    public void GetServeState_InsideTheScheduledWindow_IsServable()
    {
        LinkSnapshot link = TestLinks.Snapshot(startsAt: Now.AddDays(-1), expiresAt: Now.AddDays(1));

        Assert.Equal(LinkServeState.Servable, link.GetServeState(Now));
    }

    [Fact]
    public void GetServeState_ComparesInstantsNotLocalWallClock()
    {
        // 13:30+01:00 is 12:30 UTC, half an hour after Now; the offset must not change the verdict.
        LinkSnapshot link = TestLinks.Snapshot(startsAt: new DateTimeOffset(2026, 3, 14, 13, 30, 0, TimeSpan.FromHours(1)));

        Assert.Equal(LinkServeState.NotYetActive, link.GetServeState(Now));
        Assert.Equal(LinkServeState.Servable, link.GetServeState(Now.AddMinutes(30)));
    }
}
