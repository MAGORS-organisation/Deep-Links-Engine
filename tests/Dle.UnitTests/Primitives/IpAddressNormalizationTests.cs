using System.Net;

using Dle.Domain.Primitives;

using Xunit;

namespace Dle.UnitTests.Primitives;

/// <summary>
/// Canonical IP normalization guarantees native IPv4 and IPv4-mapped IPv6 addresses collapse to
/// identical hashes, prefixes, and rate-limit partition keys.
/// </summary>
public sealed class IpAddressNormalizationTests
{
    [Fact]
    public void Canonicalize_UnwrapsIpv4MappedIpv6ToNativeIpv4()
    {
        IPAddress mapped = IPAddress.Parse("::ffff:203.0.113.10");
        IPAddress native = IPAddress.Parse("203.0.113.10");

        Assert.Equal(native, IpAddressNormalization.Canonicalize(mapped));
        Assert.Equal(native, IpAddressNormalization.Canonicalize(native));
        Assert.Null(IpAddressNormalization.Canonicalize(null));
    }

    [Fact]
    public void PartitionKey64_MapsIpv4AndMappedIpv6Identically_AndCollapsesIpv6ToSlash64()
    {
        Assert.Equal(
            IpAddressNormalization.PartitionKey64(IPAddress.Parse("203.0.113.10"), "unknown"),
            IpAddressNormalization.PartitionKey64(IPAddress.Parse("::ffff:203.0.113.10"), "unknown"));

        Assert.Equal(
            "20010db800010002::/64",
            IpAddressNormalization.PartitionKey64(IPAddress.Parse("2001:db8:1:2::beef"), "unknown"));

        Assert.Equal("unknown", IpAddressNormalization.PartitionKey64(null, "unknown"));
    }
}
