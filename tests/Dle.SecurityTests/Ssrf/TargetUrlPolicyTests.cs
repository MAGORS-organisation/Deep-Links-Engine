using CsCheck;

namespace Dle.SecurityTests.Ssrf;

/// <summary>
/// Scheme rejection (TC-161) and the address half of SSRF (T-02, TC-162).
/// </summary>
/// <remarks>
/// <para>
/// §E.3 step 1 is a scheme allow list and step 2 is a check on the <em>resolved</em> address rather
/// than on the name. This class covers everything in those two steps that can be decided without a
/// resolver: the scheme, the credential form, the special-use names, and every address literal an
/// attacker reaches for when the string check is the only check.
/// </para>
/// <para>
/// The address matrix is deliberately exhaustive about IPv6, because that is where these checks
/// usually fail. <c>::ffff:169.254.169.254</c>, <c>::a9fe:a9fe</c>, <c>64:ff9b::a9fe:a9fe</c>,
/// <c>2002:a9fe:a9fe::</c> and a Teredo address all reach the cloud metadata endpoint, and a policy
/// that only knows about <c>169.254.0.0/16</c> in its dotted form stops none of them.
/// </para>
/// </remarks>
public sealed class TargetUrlPolicyTests
{
    /// <summary>Schemes TC-161 requires to be refused outright.</summary>
    public static TheoryData<string> ForbiddenSchemeUrls() =>
    [
        "javascript:alert(1)",
        "JavaScript:alert(1)",
        "javascript:/**/alert(1)",
        "data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==",
        "data:text/html,<script>alert(1)</script>",
        "file:///etc/passwd",
        "file://C:/Windows/win.ini",
        "intent://scan/#Intent;scheme=zxing;end",
        "vbscript:msgbox(1)",
        "about:blank",
        "blob:https://example.com/1234",
        "view-source:https://example.com",
        "ftp://example.com/file",
        "gopher://example.com:70/_",
        "dict://example.com:11211/",
        "ldap://example.com/",
        "jar:https://example.com/a.jar!/",
        "ws://example.com/socket",
        "mailto:someone@example.com",
        "tel:+421900000000",
        "myapp://open/product/1",
    ];

    /// <summary>Addresses §E.2.2 T-02 names, plus every alias that reaches them.</summary>
    public static TheoryData<string, string> ForbiddenAddresses() => new()
    {
        { "169.254.169.254", "the cloud metadata endpoint named in T-02" },
        { "169.254.170.2", "the ECS task metadata endpoint" },
        { "127.0.0.1", "loopback" },
        { "127.1.2.3", "loopback, the whole /8" },
        { "0.0.0.0", "this network" },
        { "10.0.0.1", "RFC 1918, named in TC-162" },
        { "172.16.0.1", "RFC 1918" },
        { "172.31.255.254", "RFC 1918, the top of the range" },
        { "192.168.1.1", "RFC 1918" },
        { "100.64.0.1", "carrier grade NAT" },
        { "100.127.255.255", "carrier grade NAT, the top of the range" },
        { "192.0.0.1", "IETF protocol assignments" },
        { "192.0.2.1", "TEST-NET-1" },
        { "198.18.0.1", "benchmarking" },
        { "198.51.100.1", "TEST-NET-2" },
        { "203.0.113.1", "TEST-NET-3" },
        { "224.0.0.1", "multicast" },
        { "239.255.255.250", "SSDP multicast" },
        { "255.255.255.255", "broadcast" },
        { "::1", "IPv6 loopback" },
        { "::", "the unspecified address" },
        { "fe80::1", "IPv6 link local" },
        { "fc00::1", "IPv6 unique local" },
        { "fd00::1", "IPv6 unique local" },
        { "ff02::1", "IPv6 multicast" },
        { "2001:db8::1", "IPv6 documentation" },
        { "::ffff:169.254.169.254", "IPv4-mapped IPv6 form of the metadata endpoint" },
        { "::ffff:127.0.0.1", "IPv4-mapped IPv6 loopback" },
        { "::ffff:10.0.0.1", "IPv4-mapped IPv6 private address" },
        { "::169.254.169.254", "deprecated IPv4-compatible form of the metadata endpoint" },
        { "64:ff9b::a9fe:a9fe", "NAT64 well known prefix embedding the metadata endpoint" },
        { "2002:a9fe:a9fe::", "6to4 embedding the metadata endpoint" },
        { "2001:0:4136:e378:8000:63bf:3fff:fdd2", "Teredo" },
    };

    /// <summary>Addresses that are ordinary public destinations.</summary>
    public static TheoryData<string> PublicAddresses() =>
    [
        "8.8.8.8",
        "1.1.1.1",
        "93.184.216.34",
        "172.15.0.1",
        "172.32.0.1",
        "192.167.1.1",
        "192.169.1.1",
        "100.63.255.255",
        "100.128.0.1",
        "2606:4700:4700::1111",
        "2a00:1450:4001:800::200e",
    ];

    [Theory]
    [MemberData(nameof(ForbiddenSchemeUrls))]
    [Trait("TestCase", "TC-161")]
    [Trait("Threat", "T-01")]
    public void ValidateSyntax_ASchemeOutsideTheAllowList_IsRejected(string url)
    {
        UrlSafetyVerdict verdict = TargetUrlPolicy.ValidateSyntax(url);

        Assert.Equal(UrlSafetyLevel.Blocked, verdict.Level);
        Assert.NotNull(verdict.Reason);
    }

    [Theory]
    [MemberData(nameof(ForbiddenAddresses))]
    [Trait("TestCase", "TC-162")]
    [Trait("Threat", "T-02")]
    public void IsForbiddenAddress_EveryInternalRangeAndAlias_IsForbidden(string address, string why)
    {
        Assert.True(
            TargetUrlPolicy.IsForbiddenAddress(IPAddress.Parse(address)),
            address + " must be refused: it is " + why + ".");
    }

    [Theory]
    [MemberData(nameof(ForbiddenAddresses))]
    [Trait("TestCase", "TC-162")]
    [Trait("Threat", "T-02")]
    public void ValidateSyntax_AnAddressLiteralInsideAForbiddenRange_IsRejected(string address, string why)
    {
        string url = address.Contains(':', StringComparison.Ordinal)
            ? "http://[" + address + "]/latest/meta-data/"
            : "http://" + address + "/latest/meta-data/";

        UrlSafetyVerdict verdict = TargetUrlPolicy.ValidateSyntax(url);

        Assert.True(
            verdict.Level == UrlSafetyLevel.Blocked,
            url + " must be refused: it is " + why + ".");
    }

    [Theory]
    [MemberData(nameof(PublicAddresses))]
    [Trait("Threat", "T-02")]
    public void IsForbiddenAddress_AnOrdinaryPublicAddress_IsAllowed(string address)
    {
        // The control group. A policy that refuses everything satisfies every test above and is
        // useless, so the boundaries just outside each private range are asserted too — 172.15 and
        // 172.32 either side of RFC 1918, 100.63 and 100.128 either side of the CGNAT block.
        Assert.False(TargetUrlPolicy.IsForbiddenAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [Trait("TestCase", "TC-162")]
    [Trait("Threat", "T-02")]
    [InlineData("http://localhost/")]
    [InlineData("http://LOCALHOST/")]
    [InlineData("http://localhost.")]
    [InlineData("http://foo.localhost/")]
    [InlineData("http://printer.local/")]
    [InlineData("http://vault.internal/")]
    [InlineData("http://db.home.arpa/")]
    [InlineData("http://metadata.google.internal/computeMetadata/v1/")]
    [InlineData("http://instance-data/latest/meta-data/")]
    [InlineData("http://intranet/")]
    [InlineData("http://2130706433/")]
    [InlineData("http://0x7f000001/")]
    [InlineData("http://017700000001/")]
    public void ValidateSyntax_ASpecialUseOrSingleLabelName_IsRejected(string url)
    {
        // A single-label host is an intranet name, and the integer forms of an address are single
        // labels too — which is why the same rule catches 2130706433 and 0x7f000001 without needing a
        // parser for every historical spelling of 127.0.0.1.
        UrlSafetyVerdict verdict = TargetUrlPolicy.ValidateSyntax(url);

        Assert.Equal(UrlSafetyLevel.Blocked, verdict.Level);
    }

    [Theory]
    [Trait("Threat", "T-01")]
    [InlineData("https://trusted.example.com@evil.com/")]
    [InlineData("https://trusted.example.com:pass@evil.com/")]
    [InlineData("http://user@10.0.0.1/")]
    public void ValidateSyntax_ACredentialInTheAuthority_IsRejected(string url)
    {
        // userinfo is the oldest host-disguising trick there is, and it is refused outright rather
        // than stripped: a target that needs credentials in its URL is not a target this product
        // should be pointing the public at.
        UrlSafetyVerdict verdict = TargetUrlPolicy.ValidateSyntax(url);

        Assert.Equal(UrlSafetyLevel.Blocked, verdict.Level);
        Assert.Contains("credentials", verdict.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [Trait("Threat", "T-01")]
    [InlineData("https://example.com/a\rb")]
    [InlineData("https://example.com/a\nb")]
    [InlineData("https://example.com/a\tb")]
    [InlineData("https://example.com/a b")]
    [InlineData("https://exam\u0000ple.com/")]
    public void ValidateSyntax_AControlCharacterOrWhitespace_IsRejected(string url)
    {
        Assert.Equal(UrlSafetyLevel.Blocked, TargetUrlPolicy.ValidateSyntax(url).Level);
    }

    [Fact]
    public void ValidateSyntax_ATargetLongerThanTheCap_IsRejected()
    {
        string url = "https://example.com/" + new string('a', TargetUrlPolicy.MaxUrlLength);

        Assert.Equal(UrlSafetyLevel.Blocked, TargetUrlPolicy.ValidateSyntax(url).Level);
    }

    [Theory]
    [InlineData("https://www.example.com/promo")]
    [InlineData("http://www.example.com/promo?utm_source=fb")]
    [InlineData("https://shop.example.co.uk/a/b/c#anchor")]
    [InlineData("https://8.8.8.8/")]
    public void ValidateSyntax_AnOrdinaryPublicTarget_IsAccepted(string url)
    {
        Assert.Equal(UrlSafetyLevel.Safe, TargetUrlPolicy.ValidateSyntax(url).Level);
    }

    [Fact]
    [Trait("Threat", "T-02")]
    public void IsForbiddenAddress_EveryIPv4MappedAddress_AgreesWithItsIPv4Form()
    {
        // Property, not example: the mapped form is a different sixteen bytes for the same
        // destination, so any disagreement between the two is a bypass by construction. CsCheck walks
        // the whole 32-bit space rather than the dozen literals a person would think to write down.
        Gen.UInt.Sample(
            raw =>
            {
                IPAddress v4 = new(BitConverter.GetBytes(raw));
                IPAddress mapped = v4.MapToIPv6();

                return TargetUrlPolicy.IsForbiddenAddress(v4) == TargetUrlPolicy.IsForbiddenAddress(mapped);
            },
            iter: 20_000);
    }

    [Fact]
    [Trait("Threat", "T-02")]
    public void IsForbiddenAddress_IsTotalAndNeverThrows()
    {
        // §17.9: a decision branch that cannot classify its input must deny, not fall through and not
        // fail. Sixteen arbitrary bytes is every IPv6 address there is.
        Gen.Byte.Array[16].Sample(
            bytes =>
            {
                _ = TargetUrlPolicy.IsForbiddenAddress(new IPAddress(bytes));
                return true;
            },
            iter: 20_000);
    }

    [Fact]
    [Trait("TestCase", "TC-161")]
    public void ValidateSyntax_IsTotalOverArbitraryText_AndNeverApprovesANonWebScheme()
    {
        // The syntax gate takes operator input, which means it takes whatever a compromised admin
        // session or a mistyped import file contains. It must terminate, and it must never answer
        // "safe" for anything that is not http or https.
        Gen.String.Sample(
            candidate =>
            {
                UrlSafetyVerdict verdict = TargetUrlPolicy.ValidateSyntax(candidate);

                if (verdict.Level != UrlSafetyLevel.Safe)
                {
                    return true;
                }

                return Uri.TryCreate(candidate, UriKind.Absolute, out Uri? parsed)
                    && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);
            },
            iter: 20_000);
    }
}
