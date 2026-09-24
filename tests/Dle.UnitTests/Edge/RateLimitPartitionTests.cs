using System.Net;

using Dle.Crypto;
using Dle.Domain.Crypto;
using Dle.Edge.RateLimiting;
using Dle.Edge.Telemetry;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Xunit;

namespace Dle.UnitTests.Edge;

/// <summary>
/// §E.9 fixes the limiter keys: a /24 for IPv4 and a /48 for IPv6, so that one household or one
/// mobile carrier NAT is one bucket rather than one bucket per address. The budget for 404 answers
/// is a separate partition from the budget for successful resolves — and it has to be, because a
/// legitimate campaign spike would otherwise drain the anti-enumeration defence exactly when the
/// service is most worth scanning (T-07).
/// </summary>
public sealed class RateLimitPartitionTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly EdgeMetrics _metrics = new(meterFactory: null, new DomainVerificationFailureRegistry());

    public void Dispose() => _metrics.Dispose();

    private static IpHasher Hasher() => new(
        "unit-test-ip-hash-secret-0123456789"u8.ToArray(),
        TimeSpan.FromHours(24));

    [Theory]
    [InlineData("203.0.113.42", "203.0.113.0/24")]
    [InlineData("203.0.113.1", "203.0.113.0/24")]
    [InlineData("198.51.100.200", "198.51.100.0/24")]
    public void NetworkKey_IPv4_CollapsesToTheSlashTwentyFour(string address, string expected) =>
        Assert.Equal(expected, EdgeRateLimitPartitions.NetworkKey(Hasher(), IPAddress.Parse(address)));

    [Theory]
    [InlineData("2001:db8:1234:5678::1", "2001:0db8:1234::/48")]
    [InlineData("2001:db8:1234:ffff:ffff:ffff:ffff:ffff", "2001:0db8:1234::/48")]
    public void NetworkKey_IPv6_CollapsesToTheSlashFortyEight(string address, string expected) =>
        Assert.Equal(expected, EdgeRateLimitPartitions.NetworkKey(Hasher(), IPAddress.Parse(address)));

    [Fact]
    public void NetworkKey_AddressesInOneNetwork_ShareABudget()
    {
        IpHasher hasher = Hasher();

        Assert.Equal(
            EdgeRateLimitPartitions.NetworkKey(hasher, IPAddress.Parse("203.0.113.1")),
            EdgeRateLimitPartitions.NetworkKey(hasher, IPAddress.Parse("203.0.113.250")));

        Assert.NotEqual(
            EdgeRateLimitPartitions.NetworkKey(hasher, IPAddress.Parse("203.0.113.1")),
            EdgeRateLimitPartitions.NetworkKey(hasher, IPAddress.Parse("203.0.114.1")));
    }

    [Fact]
    public void NetworkKey_IPv4MappedToIPv6_IsTheSameBudgetAsThePlainIPv4()
    {
        IpHasher hasher = Hasher();

        // Otherwise a scanner switches to the mapped form and gets a fresh budget.
        Assert.Equal(
            EdgeRateLimitPartitions.NetworkKey(hasher, IPAddress.Parse("203.0.113.42")),
            EdgeRateLimitPartitions.NetworkKey(hasher, IPAddress.Parse("::ffff:203.0.113.42")));
    }

    [Fact]
    public void NetworkKey_NoRemoteAddress_IsOneSharedBucket() =>
        Assert.Equal(
            EdgeRateLimitPartitions.UnknownClientKey,
            EdgeRateLimitPartitions.NetworkKey(Hasher(), address: null));

    [Fact]
    public void Partitions_OfDifferentKinds_NeverShareABucket()
    {
        const string key = "203.0.113.0/24";

        var resolve = new EdgeRateLimitPartition(EdgeRateLimitKind.Resolve, key);
        var qr = new EdgeRateLimitPartition(EdgeRateLimitKind.Qr, key);
        var unlimited = new EdgeRateLimitPartition(EdgeRateLimitKind.Unlimited, key);

        Assert.NotEqual(resolve, qr);
        Assert.NotEqual(resolve, unlimited);
        Assert.Equal(resolve, new EdgeRateLimitPartition(EdgeRateLimitKind.Resolve, key));
    }

    [Theory]
    [InlineData("GET", "/abc123", EdgeRateLimitKind.Resolve)]
    [InlineData("HEAD", "/abc123", EdgeRateLimitKind.Resolve)]
    [InlineData("GET", "/abc123/", EdgeRateLimitKind.Resolve)]
    [InlineData("GET", "/abc123/qr", EdgeRateLimitKind.Qr)]
    [InlineData("GET", "/abc123/QR", EdgeRateLimitKind.Qr)]
    [InlineData("POST", "/abc123", EdgeRateLimitKind.Unlimited)]
    [InlineData("GET", "/", EdgeRateLimitKind.Unlimited)]
    [InlineData("GET", "/.well-known/assetlinks.json", EdgeRateLimitKind.Unlimited)]
    [InlineData("GET", "/healthz", EdgeRateLimitKind.Unlimited)]
    public void Classify_RoutesEachPathToItsOwnBudget(string method, string path, EdgeRateLimitKind expected) =>
        Assert.Equal(expected, EdgeRateLimitPartitions.Classify(method, new PathString(path)));

    /// <summary>
    /// T-07 / TC-108. The 404 budget is a token bucket over the network prefix; draining it puts the
    /// prefix into a shadow ban, and the ban is scoped to that prefix alone.
    /// </summary>
    [Fact]
    [Trait("TestCase", "TC-108")]
    public void NotFoundBudget_WhenDrained_ShadowBansOnlyTheOffendingNetwork()
    {
        var options = new EdgeRateLimitOptions
        {
            NotFound = new NotFoundRateLimitOptions
            {
                TokensPerPeriod = 5,
                Burst = 5,
                ReplenishmentPeriodSeconds = 60,
                ShadowBanMinutes = 15,
            },
        };

        var clock = new EdgeTestClock(Now);

        using var guard = new NotFoundEnumerationGuard(
            Options.Create(options),
            clock,
            _metrics,
            NullLogger<NotFoundEnumerationGuard>.Instance);

        const string scanner = "203.0.113.0/24";
        const string bystander = "198.51.100.0/24";

        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(NotFoundVerdict.WithinBudget, guard.Register(scanner));
        }

        Assert.Equal(NotFoundVerdict.Exhausted, guard.Register(scanner));
        Assert.True(guard.IsShadowBanned(scanner));

        // A legitimate visitor on another network is untouched, which is the second half of TC-108.
        Assert.False(guard.IsShadowBanned(bystander));
        Assert.Equal(NotFoundVerdict.WithinBudget, guard.Register(bystander));
    }

    [Fact]
    [Trait("TestCase", "TC-108")]
    public void ShadowBan_ExpiresWithTheConfiguredWindow()
    {
        var options = new EdgeRateLimitOptions
        {
            NotFound = new NotFoundRateLimitOptions
            {
                TokensPerPeriod = 1,
                Burst = 1,
                ReplenishmentPeriodSeconds = 60,
                ShadowBanMinutes = 15,
            },
        };

        var clock = new EdgeTestClock(Now);

        using var guard = new NotFoundEnumerationGuard(
            Options.Create(options),
            clock,
            _metrics,
            NullLogger<NotFoundEnumerationGuard>.Instance);

        const string scanner = "203.0.113.0/24";

        Assert.Equal(NotFoundVerdict.WithinBudget, guard.Register(scanner));
        Assert.Equal(NotFoundVerdict.Exhausted, guard.Register(scanner));

        Assert.Equal(TimeSpan.FromMinutes(15), guard.ShadowBanWindow);
        Assert.True(guard.IsShadowBanned(scanner));

        clock.Now = Now.AddMinutes(14);
        Assert.True(guard.IsShadowBanned(scanner));

        clock.Now = Now.AddMinutes(16);
        Assert.False(guard.IsShadowBanned(scanner));
    }

    [Fact]
    public void NotFoundBudget_WithoutANetworkKey_IsNotCharged()
    {
        using var guard = new NotFoundEnumerationGuard(
            Options.Create(new EdgeRateLimitOptions()),
            new EdgeTestClock(Now),
            _metrics,
            NullLogger<NotFoundEnumerationGuard>.Instance);

        // An unknown client cannot be banned: the key is shared by everyone whose address the proxy
        // did not forward, so banning it would ban them all.
        Assert.Equal(NotFoundVerdict.WithinBudget, guard.Register(string.Empty));
        Assert.False(guard.IsShadowBanned(string.Empty));
    }

    [Fact]
    public void NotFoundDefaults_MatchTheDocumentedValues()
    {
        var defaults = new NotFoundRateLimitOptions();

        // §E.9: 20 per minute, burst 40, 15 minute shadow ban. The resolve budget is 600 per minute,
        // an order of magnitude larger, which is exactly why the two must not share a bucket.
        Assert.Equal(20, defaults.TokensPerPeriod);
        Assert.Equal(40, defaults.Burst);
        Assert.Equal(60, defaults.ReplenishmentPeriodSeconds);
        Assert.Equal(15, defaults.ShadowBanMinutes);

        var resolve = new ResolveRateLimitOptions();

        Assert.Equal(600, resolve.PermitsPerWindow);
        Assert.Equal(60, resolve.WindowSeconds);
        Assert.Equal(30, new QrRateLimitOptions().PermitsPerWindow);
    }
}
