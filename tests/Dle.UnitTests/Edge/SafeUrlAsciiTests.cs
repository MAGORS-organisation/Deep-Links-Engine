using System.Text;

using Dle.Edge.Rendering;
using Xunit;

namespace Dle.UnitTests.Edge;

/// <summary>
/// A URL that reaches <c>Location</c> or the content security policy must be ASCII: Kestrel refuses
/// any other header value, which turns a redirect to a perfectly ordinary Slovak or German address
/// into a 500.
/// </summary>
public sealed class SafeUrlAsciiTests
{
    [Theory]
    [InlineData("https://shop.example/path?x=1&y=two#frag")]
    [InlineData("http://shop.example/ko%C5%A1%C3%ADk")]
    public void Web_AsciiUrl_IsReturnedExactlyAsStored(string url) =>
        Assert.Equal(url, SafeUrl.Web(url));

    [Fact]
    public void Web_NonAsciiPathAndQuery_ArePercentEncoded()
    {
        string? url = SafeUrl.Web("https://www.obchod.example.sk/košík?q=čaj");

        Assert.NotNull(url);
        Assert.True(Ascii.IsValid(url));
        Assert.StartsWith("https://www.obchod.example.sk/ko%C5%A1%C3%ADk?q=", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Web_InternationalisedHost_BecomesItsIdnForm()
    {
        string? url = SafeUrl.Web("https://müller.de/produkt");

        Assert.Equal("https://xn--mller-kva.de/produkt", url);
    }

    [Fact]
    public void Web_InternationalisedHostWithPortAndNonAsciiPath_IsFullyAscii()
    {
        string? url = SafeUrl.Web("https://müller.de:8443/straße");

        Assert.NotNull(url);
        Assert.True(Ascii.IsValid(url));
        Assert.StartsWith("https://xn--mller-kva.de:8443/", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Https_InternationalisedHost_BecomesItsIdnForm()
    {
        string? url = SafeUrl.Https("https://obrázky.example/logo.png");

        Assert.NotNull(url);
        Assert.True(Ascii.IsValid(url));
        Assert.EndsWith(".example/logo.png", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Deeplink_CustomSchemeWithNonAsciiPath_IsEscaped()
    {
        string? url = SafeUrl.Deeplink("myapp://otvor/košík", ["myapp"]);

        Assert.NotNull(url);
        Assert.True(Ascii.IsValid(url));
        Assert.StartsWith("myapp://otvor/", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Web_StillRefusesWhatItRefusedBefore()
    {
        Assert.Null(SafeUrl.Web("javascript:alert(1)"));
        Assert.Null(SafeUrl.Web("/relative"));
        Assert.Null(SafeUrl.Web(null));
    }

    [Fact]
    public void AllHelpers_RefuseUrlsWithEmbeddedCredentials()
    {
        Assert.Null(SafeUrl.Web("https://user:pass@shop.example/path"));
        Assert.Null(SafeUrl.Https("https://token@cdn.example/image.png"));
        Assert.Null(SafeUrl.Deeplink("myapp://user:pass@open/item", ["myapp"]));
    }
}
