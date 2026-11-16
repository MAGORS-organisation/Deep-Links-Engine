using System.Net;

using Dle.Domain.Primitives;
using Xunit;

namespace Dle.UnitTests.Primitives;

/// <summary>
/// The forwarded headers middleware trusts every peer when both of its lists are empty. These tests
/// pin the two states that must never reach it: nothing named, and an entry that does not parse.
/// </summary>
public sealed class TrustedProxiesTests
{
    [Fact]
    public void TryParse_AddressesAndNetworks_AreBothKept()
    {
        Assert.True(TrustedProxies.TryParse(["10.0.0.5", "::1"], ["172.28.0.0/24", "fd00::/8"], out TrustedProxies? trusted));

        Assert.Equal([IPAddress.Parse("10.0.0.5"), IPAddress.IPv6Loopback], trusted.Addresses);
        Assert.Equal([IPNetwork.Parse("172.28.0.0/24"), IPNetwork.Parse("fd00::/8")], trusted.Networks);
    }

    [Fact]
    public void TryParse_NothingNamed_IsRefused()
    {
        Assert.False(TrustedProxies.TryParse([], [], out TrustedProxies? trusted));
        Assert.Null(trusted);
    }

    [Fact]
    public void TryParse_NullLists_AreRefused()
    {
        Assert.False(TrustedProxies.TryParse(null, null, out _));
    }

    [Fact]
    public void TryParse_OnlyBlankEntries_IsRefused()
    {
        // An unset environment variable expands to an empty list entry; that is "nothing named".
        Assert.False(TrustedProxies.TryParse([""], ["  "], out _));
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("10.0.0.0/8")]
    [InlineData("300.1.1.1")]
    public void TryParse_UnparseableProxy_IsRefusedEvenWhenAnotherEntryIsValid(string proxy)
    {
        Assert.False(TrustedProxies.TryParse([proxy], ["172.28.0.0/24"], out _));
    }

    [Theory]
    [InlineData("172.28.0.0")]
    [InlineData("172.28.0.0/33")]
    [InlineData("everything")]
    public void TryParse_UnparseableNetwork_IsRefusedEvenWhenAnotherEntryIsValid(string network)
    {
        Assert.False(TrustedProxies.TryParse(["10.0.0.5"], [network], out _));
    }

    [Fact]
    public void TryParse_BlankEntriesBesideValidOnes_AreIgnored()
    {
        Assert.True(TrustedProxies.TryParse(["", " 10.0.0.5 "], [""], out TrustedProxies? trusted));

        Assert.Equal([IPAddress.Parse("10.0.0.5")], trusted.Addresses);
        Assert.Empty(trusted.Networks);
    }
}
