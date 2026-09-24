using Dle.Domain.Primitives;
using Xunit;

namespace Dle.UnitTests.Primitives;

/// <summary>
/// The normalised host is the tenant lookup key on the hot path. Two spellings of one host that
/// normalise differently are two links; two different hosts that normalise the same are a tenant
/// boundary violation. SHARED-KERNEL section 1.
/// </summary>
public sealed class HostNormalizerTests
{
    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("EXAMPLE.COM", "example.com")]
    [InlineData("Example.Com", "example.com")]
    [InlineData("example.com.", "example.com")]
    [InlineData("example.com...", "example.com")]
    [InlineData("  example.com  ", "example.com")]
    [InlineData("example.com:8443", "example.com")]
    [InlineData("example.com:443", "example.com")]
    [InlineData("www.example.com", "example.com")]
    [InlineData("WWW.Example.COM:8080", "example.com")]
    [InlineData("dl.example.com", "dl.example.com")]
    public void TryNormalize_CommonSpellings_CollapseToOneIdentity(string input, string expected)
    {
        Assert.True(HostNormalizer.TryNormalize(input, out string normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void TryNormalize_StripsOnlyASingleLeadingWwwLabel()
    {
        Assert.True(HostNormalizer.TryNormalize("www.www.example.com", out string normalized));
        Assert.Equal("www.example.com", normalized);
    }

    [Fact]
    public void TryNormalize_HostThatIsOnlyTheWwwLabel_LosesTheRootDotBeforeThePrefixCheck()
    {
        // The trailing dot goes first, so what is left is the label "www", not the prefix "www.".
        Assert.True(HostNormalizer.TryNormalize("www.", out string normalized));
        Assert.Equal("www", normalized);
    }

    [Theory]
    [InlineData("háčky.sk", "xn--hky-ela4t.sk")]
    [InlineData("HÁČKY.SK", "xn--hky-ela4t.sk")]
    [InlineData("münchen.de", "xn--mnchen-3ya.de")]
    public void TryNormalize_InternationalisedName_IsConvertedToPunycode(string input, string expected)
    {
        Assert.True(HostNormalizer.TryNormalize(input, out string normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void TryNormalize_PunycodeInput_IsAlreadyCanonicalAndSurvivesUnchanged()
    {
        Assert.True(HostNormalizer.TryNormalize("xn--hky-ela4t.sk", out string normalized));
        Assert.Equal("xn--hky-ela4t.sk", normalized);
    }

    [Fact]
    public void TryNormalize_HostFollowedByAPath_KeepsOnlyTheAuthority()
    {
        Assert.True(HostNormalizer.TryNormalize("example.com/some/path", out string normalized));
        Assert.Equal("example.com", normalized);
    }

    [Fact]
    public void TryNormalize_FullUrlWithSchemeCredentialsPortAndPath_YieldsOnlyTheHost()
    {
        Assert.True(HostNormalizer.TryNormalize("https://user:pass@WWW.Example.com:8443/a/b?q=1#f", out string normalized));
        Assert.Equal("example.com", normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("exa mple.com")]
    [InlineData("exam\tple.com")]
    [InlineData("[::1]")]
    [InlineData("[2001:db8::1]")]
    [InlineData("exam_ple.com")]
    [InlineData("under_score.example.com")]
    public void TryNormalize_InvalidHost_IsRejectedWithoutThrowing(string? input)
    {
        Assert.False(HostNormalizer.TryNormalize(input, out string normalized));
        Assert.Equal(string.Empty, normalized);
    }

    [Fact]
    public void TryNormalize_HostAtTheLengthLimit_IsAccepted()
    {
        string host = Label(63) + "." + Label(63) + "." + Label(63) + "." + Label(61);

        Assert.Equal(HostNormalizer.MaxLength, host.Length);
        Assert.True(HostNormalizer.TryNormalize(host, out string normalized));
        Assert.Equal(host, normalized);
    }

    [Fact]
    public void TryNormalize_HostOverTheLengthLimit_IsRejected()
    {
        string host = Label(63) + "." + Label(63) + "." + Label(63) + "." + Label(62);

        Assert.Equal(HostNormalizer.MaxLength + 1, host.Length);
        Assert.False(HostNormalizer.TryNormalize(host, out _));
    }

    [Fact]
    public void TryNormalize_LabelOverSixtyThreeCharacters_IsRejected()
    {
        Assert.False(HostNormalizer.TryNormalize(Label(64) + ".example.com", out _));
    }

    [Fact]
    public void Normalize_ValidHost_ReturnsTheNormalisedForm()
    {
        Assert.Equal("example.com", HostNormalizer.Normalize("WWW.Example.COM."));
    }

    [Fact]
    public void Normalize_InvalidHost_ThrowsArgumentException()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => HostNormalizer.Normalize("exa mple.com"));
        Assert.Equal("host", error.ParamName);
    }

    [Fact]
    public void Normalize_NullHost_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => HostNormalizer.Normalize(null!));
    }

    [Fact]
    public void MaxLength_IsTheDnsLimit()
    {
        Assert.Equal(253, HostNormalizer.MaxLength);
    }

    private static string Label(int length) => new('a', length);
}
