using System.Net;
using Dle.Domain.Abuse;
using Xunit;

namespace Dle.UnitTests.Abuse;

/// <summary>
/// TC-161 and TC-162, threats T-01 and T-02. A link shortener is an SSRF primitive and an XSS
/// primitive unless the target is constrained at write time. Everything below is a rejection the
/// product depends on, not a nicety.
/// </summary>
public sealed class TargetUrlPolicyTests
{
    // ================================================================ TC-161: dangerous schemes

    [Theory]
    [Trait("TestCase", "TC-161")]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("javascript:void(document.cookie)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("file:///etc/passwd")]
    [InlineData("file://C:/Windows/win.ini")]
    [InlineData("intent://scan/#Intent;scheme=zxing;package=com.evil;end")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("about:blank")]
    [InlineData("about:config")]
    [InlineData("blob:https://example.com/550e8400-e29b-41d4-a716-446655440000")]
    [InlineData("ftp://files.example.com/x")]
    [InlineData("gopher://example.com/1")]
    [InlineData("ws://example.com/socket")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("tel:+421900000000")]
    [InlineData("market://details?id=sk.example")]
    [InlineData("myapp://product/123")]
    public void ValidateSyntax_SchemeOutsideHttpAndHttps_IsBlocked(string url)
    {
        UrlSafetyVerdict verdict = TargetUrlPolicy.ValidateSyntax(url);

        Assert.Equal(UrlSafetyLevel.Blocked, verdict.Level);
        Assert.NotNull(verdict.Reason);
    }

    [Theory]
    [InlineData("https://shop.example.com/product/123")]
    [InlineData("http://shop.example.com/product/123")]
    [InlineData("HTTPS://SHOP.EXAMPLE.COM/")]
    [InlineData("https://shop.example.com/a?b=c#d")]
    [InlineData("https://xn--hky-ela4t.sk/")]
    [InlineData("https://sub.domain.shop.example.com:8443/p")]
    [InlineData("https://93.184.216.34/")]
    [InlineData("https://[2606:2800:220:1:248:1893:25c8:1946]/")]
    public void ValidateSyntax_PublicHttpTarget_IsSafe(string url)
    {
        UrlSafetyVerdict verdict = TargetUrlPolicy.ValidateSyntax(url);

        Assert.Equal(UrlSafetyLevel.Safe, verdict.Level);
    }

    [Fact]
    public void AllowedSchemes_AreExactlyHttpAndHttps()
    {
        string[] expected = ["http", "https"];

        Assert.Equal(expected, TargetUrlPolicy.AllowedSchemes);
    }

    // ================================================================ malformed and oversized

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("//example.com/x")]
    [InlineData("https://")]
    public void ValidateSyntax_UnusableInput_IsBlocked(string? url)
    {
        Assert.Equal(UrlSafetyLevel.Blocked, TargetUrlPolicy.ValidateSyntax(url).Level);
    }

    [Theory]
    [InlineData("https://example.com/\u0000evil")]
    [InlineData("https://example.com/ evil")]
    [InlineData("https://example.com/\nevil")]
    [InlineData("https://example.com/\revil")]
    [InlineData("https://example.com/\tevil")]
    public void ValidateSyntax_ControlCharacterOrWhitespace_IsBlocked(string url)
    {
        UrlSafetyVerdict verdict = TargetUrlPolicy.ValidateSyntax(url);

        Assert.Equal(UrlSafetyLevel.Blocked, verdict.Level);
        Assert.Contains("control character", verdict.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateSyntax_UrlAtTheLengthLimit_IsAccepted()
    {
        const string Prefix = "https://shop.example.com/";
        string url = Prefix + new string('a', TargetUrlPolicy.MaxUrlLength - Prefix.Length);

        Assert.Equal(TargetUrlPolicy.MaxUrlLength, url.Length);
        Assert.Equal(UrlSafetyLevel.Safe, TargetUrlPolicy.ValidateSyntax(url).Level);
    }

    [Fact]
    public void ValidateSyntax_UrlOverTheLengthLimit_IsBlocked()
    {
        const string Prefix = "https://shop.example.com/";
        string url = Prefix + new string('a', (TargetUrlPolicy.MaxUrlLength - Prefix.Length) + 1);

        UrlSafetyVerdict verdict = TargetUrlPolicy.ValidateSyntax(url);

        Assert.Equal(UrlSafetyLevel.Blocked, verdict.Level);
        Assert.Contains("2048", verdict.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void MaxUrlLength_IsTheDocumentedLimit()
    {
        Assert.Equal(2048, TargetUrlPolicy.MaxUrlLength);
    }

    // ================================================================ embedded credentials

    [Theory]
    [InlineData("https://user:pass@evil.example.com/")]
    [InlineData("https://shop.example.com@evil.example.com/")]
    [InlineData("http://admin@evil.example.com/")]
    public void ValidateSyntax_EmbeddedCredentials_AreBlocked(string url)
    {
        UrlSafetyVerdict verdict = TargetUrlPolicy.ValidateSyntax(url);

        Assert.Equal(UrlSafetyLevel.Blocked, verdict.Level);
        Assert.Contains("credentials", verdict.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================ TC-162: forbidden addresses

    [Theory]
    [Trait("TestCase", "TC-162")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]          // AWS / Azure / DO metadata
    [InlineData("http://169.254.170.2/v2/credentials")]               // ECS task metadata
    [InlineData("http://metadata.google.internal/computeMetadata/v1/")]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("http://127.1.2.3/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://10.255.255.254/")]
    [InlineData("http://172.16.0.1/")]
    [InlineData("http://172.31.255.254/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://100.64.0.1/")]                                // RFC 6598 CGNAT
    [InlineData("http://100.127.255.254/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://192.0.0.1/")]                                 // IETF protocol assignments
    [InlineData("http://192.0.2.1/")]                                 // TEST-NET-1
    [InlineData("http://198.51.100.1/")]                              // TEST-NET-2
    [InlineData("http://203.0.113.1/")]                               // TEST-NET-3
    [InlineData("http://198.18.0.1/")]                                // benchmarking
    [InlineData("http://198.19.255.254/")]
    [InlineData("http://224.0.0.1/")]                                 // multicast
    [InlineData("http://255.255.255.255/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[fe80::1]/")]                                 // link local
    [InlineData("http://[fc00::1]/")]                                 // unique local
    [InlineData("http://[fd12:3456:789a::1]/")]
    [InlineData("http://[2001:db8::1]/")]                             // documentation
    [InlineData("http://[ff02::1]/")]                                 // multicast
    [InlineData("http://[::ffff:10.0.0.1]/")]                         // IPv4 mapped private
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://[::ffff:169.254.169.254]/")]                  // IPv4 mapped metadata
    [InlineData("http://[::ffff:192.168.0.1]/")]
    [InlineData("http://[::10.0.0.1]/")]                              // deprecated IPv4 compatible
    [InlineData("http://[64:ff9b::a00:1]/")]                          // NAT64 wrapping 10.0.0.1
    [InlineData("http://[2002:0a00:0001::1]/")]                       // 6to4 wrapping 10.0.0.1
    [InlineData("http://localhost/")]
    [InlineData("http://localhost:8080/")]
    [InlineData("http://intranet/")]                                  // single label
    [InlineData("http://wiki.local/")]
    [InlineData("http://api.internal/")]
    [InlineData("http://box.home.arpa/")]
    [InlineData("http://instance-data/latest/")]
    public void ValidateSyntax_ForbiddenAddressOrName_IsBlocked(string url)
    {
        UrlSafetyVerdict verdict = TargetUrlPolicy.ValidateSyntax(url);

        Assert.Equal(UrlSafetyLevel.Blocked, verdict.Level);
    }

    [Theory]
    [Trait("TestCase", "TC-162")]
    [InlineData("http://100.128.0.1/")]        // just outside CGNAT
    [InlineData("http://100.63.255.254/")]
    [InlineData("http://172.32.0.1/")]         // just outside RFC 1918
    [InlineData("http://172.15.255.254/")]
    [InlineData("http://11.0.0.1/")]
    [InlineData("http://192.169.0.1/")]
    [InlineData("http://198.20.0.1/")]
    [InlineData("http://203.0.114.1/")]
    [InlineData("http://223.255.255.254/")]
    public void ValidateSyntax_AddressJustOutsideAForbiddenRange_IsAllowed(string url)
    {
        Assert.Equal(UrlSafetyLevel.Safe, TargetUrlPolicy.ValidateSyntax(url).Level);
    }

    [Theory]
    [Trait("TestCase", "TC-162")]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.20.0.5")]
    [InlineData("192.168.0.5")]
    [InlineData("100.100.0.5")]
    [InlineData("0.0.0.0")]
    [InlineData("192.0.2.5")]
    [InlineData("198.51.100.5")]
    [InlineData("203.0.113.5")]
    [InlineData("198.18.5.5")]
    [InlineData("240.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("2001:db8::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    public void IsForbiddenAddress_KnownInternalAddress_IsForbidden(string address)
    {
        Assert.True(TargetUrlPolicy.IsForbiddenAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:2800:220:1:248:1893:25c8:1946")]
    [InlineData("2a00:1450:4001:800::200e")]
    public void IsForbiddenAddress_PublicAddress_IsAllowed(string address)
    {
        Assert.False(TargetUrlPolicy.IsForbiddenAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public void IsForbiddenAddress_NullAddress_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TargetUrlPolicy.IsForbiddenAddress(null!));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("intranet")]
    [InlineData("wiki.local")]
    [InlineData("build.internal")]
    [InlineData("metadata.google.internal")]
    [InlineData("box.home.arpa")]
    [InlineData("app.localhost")]
    [InlineData("instance-data")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("   ")]
    public void IsForbiddenHost_SpecialUseName_IsForbidden(string host)
    {
        Assert.True(TargetUrlPolicy.IsForbiddenHost(host));
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("shop.example.com")]
    [InlineData("localhost.example.com")]
    [InlineData("internal.example.com")]
    [InlineData("xn--hky-ela4t.sk")]
    public void IsForbiddenHost_PublicName_IsAllowed(string host)
    {
        Assert.False(TargetUrlPolicy.IsForbiddenHost(host));
    }

    // ================================================================ verdict shape

    [Fact]
    public void ValidateSyntax_Rejection_NamesTheSourceThatDecided()
    {
        Assert.Equal("syntax", TargetUrlPolicy.ValidateSyntax("javascript:alert(1)").Source);
        Assert.Equal("private_ip", TargetUrlPolicy.ValidateSyntax("http://10.0.0.1/").Source);
        Assert.Equal("private_ip", TargetUrlPolicy.ValidateSyntax("http://metadata.google.internal/").Source);
    }

    [Fact]
    public void Safe_AndReject_BuildTheDocumentedVerdicts()
    {
        UrlSafetyVerdict safe = UrlSafetyVerdict.Safe("urlhaus");
        Assert.Equal(UrlSafetyLevel.Safe, safe.Level);
        Assert.Equal("urlhaus", safe.Source);
        Assert.Null(safe.Reason);

        UrlSafetyVerdict rejected = UrlSafetyVerdict.Reject(UrlSafetyLevel.Malicious, "blocklist", "listed");
        Assert.Equal(UrlSafetyLevel.Malicious, rejected.Level);
        Assert.Equal("blocklist", rejected.Source);
        Assert.Equal("listed", rejected.Reason);
    }
}
