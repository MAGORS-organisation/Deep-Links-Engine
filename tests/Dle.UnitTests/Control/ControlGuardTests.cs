using Dle.Control.Configuration;
using Dle.Control.Features.Analytics;
using Dle.Control.Features.Domains;
using Dle.Control.Identity;
using Dle.Control.Workers;
using Dle.Domain.Contracts;
using Dle.Domain.Entities;

using Microsoft.Extensions.Options;

using Xunit;

namespace Dle.UnitTests.Control;

/// <summary>
/// Small rules in the control plane whose failure is quiet: identifiers that collide between
/// replicas, an alarm that fires for the wrong hosts, and a limiter that can be walked around.
/// </summary>
public sealed class ControlGuardTests
{
    [Theory]
    [InlineData("dle-control-7c9f8d5b6-2xk4q")]
    [InlineData("dle-control-7c9f8d5b6-9wz7p")]
    [InlineData("")]
    [InlineData(null)]
    public void DeriveNodeId_IsStableAndInRange(string? hostName)
    {
        int first = DleControlOptions.DeriveNodeId(hostName);

        Assert.InRange(first, 0, 1023);
        Assert.Equal(first, DleControlOptions.DeriveNodeId(hostName));
    }

    [Fact]
    public void DeriveNodeId_TwoReplicasOfOneDeployment_GetDifferentIdentifiers()
    {
        // The case that used to collide: every replica minted link identifiers as node 0, so two
        // creates in the same millisecond produced the same identifier.
        Assert.NotEqual(
            DleControlOptions.DeriveNodeId("dle-control-7c9f8d5b6-2xk4q"),
            DleControlOptions.DeriveNodeId("dle-control-7c9f8d5b6-9wz7p"));
    }

    [Fact]
    public void WasServed_AHostThatNeverPassedACheck_WasNot()
    {
        Assert.False(DomainVerificationWorker.WasServed(new LinkDomain { Host = "new.example" }));
        Assert.False(DomainVerificationWorker.WasServed(new LinkDomain
        {
            Host = "broken.example",
            TlsStatus = DomainVerificationService.FailedStatus,
            AasaStatus = DomainVerificationService.FailedStatus,
            AssetlinksStatus = DomainVerificationService.FailedStatus,
            LastVerifiedAt = DateTimeOffset.UnixEpoch,
        }));
    }

    [Fact]
    public void LooksLikeTakeover_OnlyForAHostThatWasServedAndNoLongerResolves()
    {
        DomainVerificationResponse unresolved = Response(DomainVerificationService.FailedStatus, "dns.unresolved");

        // Checked every night but never set up: not a takeover, however often it has been checked.
        Assert.False(DomainVerificationWorker.LooksLikeTakeover(servedBefore: false, unresolved));

        // Was serving, has stopped resolving: the name may be free to claim at the provider.
        Assert.True(DomainVerificationWorker.LooksLikeTakeover(servedBefore: true, unresolved));

        // Was serving and DNS still passes: something else failed, not a takeover.
        Assert.False(DomainVerificationWorker.LooksLikeTakeover(
            servedBefore: true,
            Response(DomainVerificationService.OkStatus)));
    }

    [Fact]
    public void AuthAttemptLimiter_InventingANewKeyPrefixPerRequest_DoesNotBuyUnlimitedVerifications()
    {
        using DleAuthAttemptLimiter limiter = new(
            Options.Create(new DleRateLimitOptions { AuthAttemptsPerMinute = 10, AuthAttemptBurst = 10 }),
            new ControlTestClock(DateTimeOffset.UnixEpoch));

        int allowed = 0;

        for (int i = 0; i < 1000; i++)
        {
            if (limiter.TryAcquire("203.0.113.0/24", "prefix" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            {
                allowed++;
            }
        }

        // Each allowed attempt costs a full Argon2id verification. One address gets a bounded number
        // of them however many prefixes it makes up.
        Assert.Equal(60, allowed);

        // Another address is unaffected.
        Assert.True(limiter.TryAcquire("198.51.100.0/24", "prefix0"));
    }

    [Fact]
    public void AuthAttemptLimiter_OneKeyPrefix_KeepsItsOwnSmallerBudget()
    {
        using DleAuthAttemptLimiter limiter = new(
            Options.Create(new DleRateLimitOptions { AuthAttemptsPerMinute = 10, AuthAttemptBurst = 10 }),
            new ControlTestClock(DateTimeOffset.UnixEpoch));

        int allowed = 0;

        for (int i = 0; i < 100; i++)
        {
            if (limiter.TryAcquire("203.0.113.0/24", "sameprefix"))
            {
                allowed++;
            }
        }

        Assert.Equal(10, allowed);
    }

    [Fact]
    public void StreamAnalytics_TryAcquireStreamSlot_CapsConcurrentStreamsPerTenant()
    {
        Guid tenantA = Guid.NewGuid();
        Guid tenantB = Guid.NewGuid();
        List<StreamAnalytics.StreamLease> leases = [];

        try
        {
            for (int i = 0; i < StreamAnalytics.MaxConcurrentStreamsPerTenant; i++)
            {
                StreamAnalytics.StreamLease? lease = StreamAnalytics.TryAcquireStreamSlot(tenantA);
                Assert.NotNull(lease);
                leases.Add(lease);
            }

            Assert.Null(StreamAnalytics.TryAcquireStreamSlot(tenantA));

            // Disposing the same lease twice must only release one slot.
            leases[0].Dispose();
            leases[0].Dispose();

            StreamAnalytics.StreamLease? replacement = StreamAnalytics.TryAcquireStreamSlot(tenantA);
            Assert.NotNull(replacement);
            leases.Add(replacement);
            Assert.Null(StreamAnalytics.TryAcquireStreamSlot(tenantA));

            // Another tenant keeps its own isolated quota.
            using StreamAnalytics.StreamLease? other = StreamAnalytics.TryAcquireStreamSlot(tenantB);
            Assert.NotNull(other);
        }
        finally
        {
            foreach (StreamAnalytics.StreamLease lease in leases)
            {
                lease.Dispose();
            }
        }
    }

    private static DomainVerificationResponse Response(string dnsStatus, params string[] codes) => new()
    {
        DomainId = Guid.NewGuid(),
        Host = "link.example",
        Ok = false,
        CheckedAt = DateTimeOffset.UnixEpoch,
        Checks =
        [
            new VerificationCheckResult
            {
                Kind = DomainVerificationService.DnsKind,
                Status = dnsStatus,
                Codes = codes,
            },
        ],
    };
}
