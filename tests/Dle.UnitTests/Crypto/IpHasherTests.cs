using System.Net;

using Dle.Crypto;
using Dle.Domain.Crypto;

using Xunit;

namespace Dle.UnitTests.Crypto;

/// <summary>
/// An IP address is personal data (Breyer, C-582/14), so the engine stores only two derived forms:
/// a keyed hash under a rotating salt, and a truncated network prefix (§B.5.3, FR-247, K9). The
/// rotation is a privacy control rather than key hygiene — once the salt changes, yesterday's
/// hashes can no longer be linked to today's, so the correlation window is bounded by construction.
/// </summary>
public sealed class IpHasherTests
{
    private static readonly DateTimeOffset Monday = new(2026, 2, 2, 10, 0, 0, TimeSpan.Zero);

    private static IpHasher Hasher(TimeSpan? rotation = null) =>
        new(CryptoTestKeys.Primary, rotation ?? TimeSpan.FromHours(24));

    [Fact]
    public void Hash_SameAddressSameDay_IsStable()
    {
        IpHasher hasher = Hasher();
        IPAddress address = IPAddress.Parse("203.0.113.42");

        byte[] morning = hasher.Hash(address, Monday);
        byte[] evening = hasher.Hash(address, Monday.AddHours(13));

        Assert.Equal(morning, evening);
        Assert.Equal(32, morning.Length);
    }

    [Fact]
    public void Hash_SameAddressOnTheNextDay_Differs()
    {
        IpHasher hasher = Hasher();
        IPAddress address = IPAddress.Parse("203.0.113.42");

        // The salt rotates daily, so the same visitor is unlinkable across the boundary.
        Assert.NotEqual(hasher.Hash(address, Monday), hasher.Hash(address, Monday.AddDays(1)));
    }

    [Fact]
    public void Hash_AcrossTheRotationBoundary_ChangesExactlyOnce()
    {
        IpHasher hasher = Hasher(TimeSpan.FromHours(1));
        IPAddress address = IPAddress.Parse("198.51.100.7");

        var justBefore = new DateTimeOffset(2026, 2, 2, 10, 59, 59, TimeSpan.Zero);
        var justAfter = new DateTimeOffset(2026, 2, 2, 11, 0, 0, TimeSpan.Zero);

        Assert.Equal(hasher.Hash(address, justBefore.AddMinutes(-30)), hasher.Hash(address, justBefore));
        Assert.NotEqual(hasher.Hash(address, justBefore), hasher.Hash(address, justAfter));
    }

    [Fact]
    public void Hash_DifferentAddresses_Differ()
    {
        IpHasher hasher = Hasher();

        Assert.NotEqual(
            hasher.Hash(IPAddress.Parse("203.0.113.42"), Monday),
            hasher.Hash(IPAddress.Parse("203.0.113.43"), Monday));
    }

    [Fact]
    public void Hash_WithADifferentSecret_Differs()
    {
        IPAddress address = IPAddress.Parse("203.0.113.42");

        var first = new IpHasher(CryptoTestKeys.Primary, TimeSpan.FromHours(24));
        var second = new IpHasher(CryptoTestKeys.Secondary, TimeSpan.FromHours(24));

        Assert.NotEqual(first.Hash(address, Monday), second.Hash(address, Monday));
    }

    [Theory]
    [InlineData("203.0.113.42")]
    [InlineData("10.11.12.13")]
    [InlineData("2001:db8:1234:5678:9abc:def0:1234:5678")]
    [InlineData("fe80::1")]
    public void Hash_NeverContainsTheAddressBytes(string text)
    {
        IPAddress address = IPAddress.Parse(text);
        byte[] digest = Hasher().Hash(address, Monday);
        byte[] raw = address.GetAddressBytes();

        // The point of hashing is that the address cannot be read back out of the digest. A digest
        // that literally contained the address bytes would fail that in the most obvious way.
        Assert.False(Contains(digest, raw), "the digest contains the raw address bytes");
    }

    [Fact]
    public void Hash_IPv4MappedToIPv6_MatchesThePlainIPv4Hash()
    {
        IpHasher hasher = Hasher();

        // The same client behind a dual stack proxy must hash to one value, not two.
        Assert.Equal(
            hasher.Hash(IPAddress.Parse("203.0.113.42"), Monday),
            hasher.Hash(IPAddress.Parse("::ffff:203.0.113.42"), Monday));
    }

    [Fact]
    public void Hash_NullAddress_Throws() =>
        Assert.Throws<ArgumentNullException>(() => Hasher().Hash(null!, Monday));

    [Theory]
    [InlineData("203.0.113.42", "203.0.113.0/24")]
    [InlineData("203.0.113.255", "203.0.113.0/24")]
    [InlineData("10.0.0.1", "10.0.0.0/24")]
    [InlineData("1.2.3.4", "1.2.3.0/24")]
    public void Prefix_IPv4_IsTheSlashTwentyFourNetwork(string address, string expected) =>
        Assert.Equal(expected, Hasher().Prefix(IPAddress.Parse(address)));

    [Theory]
    [InlineData("2001:db8:1234:5678:9abc:def0:1234:5678", "2001:0db8:1234::/48")]
    [InlineData("2001:db8:1234::1", "2001:0db8:1234::/48")]
    [InlineData("fe80::1", "fe80:0000:0000::/48")]
    public void Prefix_IPv6_IsTheSlashFortyEightNetwork(string address, string expected) =>
        Assert.Equal(expected, Hasher().Prefix(IPAddress.Parse(address)));

    [Fact]
    public void Prefix_AddressesInTheSameNetwork_ShareAKey()
    {
        IpHasher hasher = Hasher();

        Assert.Equal(hasher.Prefix(IPAddress.Parse("203.0.113.1")), hasher.Prefix(IPAddress.Parse("203.0.113.254")));
        Assert.NotEqual(hasher.Prefix(IPAddress.Parse("203.0.113.1")), hasher.Prefix(IPAddress.Parse("203.0.114.1")));

        Assert.Equal(
            hasher.Prefix(IPAddress.Parse("2001:db8:1234::1")),
            hasher.Prefix(IPAddress.Parse("2001:db8:1234:ffff::9")));

        Assert.NotEqual(
            hasher.Prefix(IPAddress.Parse("2001:db8:1234::1")),
            hasher.Prefix(IPAddress.Parse("2001:db8:1235::1")));
    }

    [Fact]
    public void Prefix_IPv4MappedToIPv6_IsTheIPv4Network() =>
        Assert.Equal("203.0.113.0/24", Hasher().Prefix(IPAddress.Parse("::ffff:203.0.113.42")));

    [Fact]
    public void Prefix_NullAddress_ReturnsNull() =>
        // The parameter is annotated non-nullable, but the rate limiter reaches this path with a
        // connection that has no remote address, so the null branch is a real one.
        Assert.Null(Hasher().Prefix(null!));

    [Fact]
    public void Constructor_ShortSecret_Throws() =>
        Assert.Throws<ArgumentException>(() => new IpHasher(new byte[15], TimeSpan.FromHours(24)));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveRotation_Throws(int hours) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new IpHasher(CryptoTestKeys.Primary, TimeSpan.FromHours(hours)));

    [Fact]
    public void Hasher_IsTheContractedInterface() =>
        Assert.IsAssignableFrom<IIpHasher>(Hasher());

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (int start = 0; start + needle.Length <= haystack.Length; start++)
        {
            if (haystack.AsSpan(start, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }
}
