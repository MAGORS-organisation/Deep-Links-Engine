using System.Globalization;
using System.Text;
using Dle.Domain.Attribution;
using Xunit;

namespace Dle.UnitTests.Attribution;

/// <summary>
/// TC-141, TC-142, TC-167. The Play Install Referrer is the only deterministic attribution channel
/// Android has and it arrives from a source the engine does not control. The parser therefore has
/// exactly one hard contract: it never throws, whatever it is handed.
/// </summary>
public sealed class InstallReferrerParserTests
{
    [Fact]
    public void TryGetClickId_ReferrerCarryingTheClickId_ReturnsIt()
    {
        Assert.True(InstallReferrerParser.TryGetClickId("utm_source=poster&dl_cid=01JQ8Z0K7M8T9RV", out string clickId));
        Assert.Equal("01JQ8Z0K7M8T9RV", clickId);
    }

    [Fact]
    public void Parse_TypicalPlayReferrer_YieldsEveryPair()
    {
        IReadOnlyDictionary<string, string> pairs =
            InstallReferrerParser.Parse("dl_cid=01JQ8Z0K7M8T9RV&utm_source=poster&utm_medium=print&utm_campaign=spring");

        Assert.Equal(4, pairs.Count);
        Assert.Equal("01JQ8Z0K7M8T9RV", pairs["dl_cid"]);
        Assert.Equal("poster", pairs["utm_source"]);
        Assert.Equal("print", pairs["utm_medium"]);
        Assert.Equal("spring", pairs["utm_campaign"]);
    }

    [Fact]
    public void TryGetClickId_OrganicInstallWithoutTheClickId_ReturnsFalse()
    {
        Assert.False(InstallReferrerParser.TryGetClickId("utm_source=google-play&utm_medium=organic", out string clickId));
        Assert.Equal(string.Empty, clickId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NoReferrerAtAll_YieldsAnEmptyMap(string? referrer)
    {
        Assert.Empty(InstallReferrerParser.Parse(referrer));
        Assert.False(InstallReferrerParser.TryGetClickId(referrer, out _));
    }

    [Fact]
    public void TryGetClickId_EmptyClickIdValue_IsTreatedAsAbsent()
    {
        Assert.False(InstallReferrerParser.TryGetClickId("dl_cid=&utm_source=poster", out _));
        Assert.False(InstallReferrerParser.TryGetClickId("dl_cid=%20%20&utm_source=poster", out _));
    }

    [Fact]
    public void TryGetClickId_TrimsSurroundingWhitespace()
    {
        Assert.True(InstallReferrerParser.TryGetClickId("dl_cid=%20abc%20", out string clickId));
        Assert.Equal("abc", clickId);
    }

    // ================================================================ never throws

    [Theory]
    [InlineData("%")]
    [InlineData("%%%")]
    [InlineData("%zz")]
    [InlineData("a=%E0%A4%A")]
    [InlineData("=")]
    [InlineData("=novalue")]
    [InlineData("&&&&")]
    [InlineData("noseparator")]
    [InlineData("a=1&&b=2&")]
    [InlineData("a==1")]
    [InlineData("\0=\0")]
    [InlineData("dl_cid=\ud800")]
    public void Parse_MalformedInput_NeverThrows(string referrer)
    {
        // The contract is that neither call throws, whatever the store handed the SDK.
        IReadOnlyDictionary<string, string> pairs = InstallReferrerParser.Parse(referrer);
        bool found = InstallReferrerParser.TryGetClickId(referrer, out string clickId);

        Assert.True(pairs.Count <= InstallReferrerParser.MaxPairs);
        Assert.Equal(found, clickId.Length > 0);
    }

    [Fact]
    public void Parse_SegmentWithoutASeparatorOrWithAnEmptyKey_IsDropped()
    {
        IReadOnlyDictionary<string, string> pairs = InstallReferrerParser.Parse("noseparator&=orphan&dl_cid=abc");

        Assert.Equal("abc", Assert.Single(pairs).Value);
    }

    [Fact]
    public void Parse_ValueContainingAnEqualsSign_KeepsTheTail()
    {
        Assert.Equal("1=2", InstallReferrerParser.Parse("a=1=2")["a"]);
    }

    // ================================================================ duplicates

    [Fact]
    public void Parse_DuplicateKey_KeepsTheFirstOccurrence()
    {
        IReadOnlyDictionary<string, string> pairs = InstallReferrerParser.Parse("dl_cid=first&dl_cid=second&dl_cid=third");

        Assert.Equal("first", pairs["dl_cid"]);
        Assert.Single(pairs);
    }

    [Fact]
    public void Parse_KeysAreCaseSensitive()
    {
        IReadOnlyDictionary<string, string> pairs = InstallReferrerParser.Parse("dl_cid=lower&DL_CID=upper");

        Assert.Equal("lower", pairs["dl_cid"]);
        Assert.Equal("upper", pairs["DL_CID"]);
    }

    // ================================================================ encoding

    [Fact]
    public void Parse_ReferrerEncodedAsAWhole_IsDecodedOnce()
    {
        // Play hands the referrer over percent encoded; "utm_source%3Dposter%26dl_cid%3Dabc".
        IReadOnlyDictionary<string, string> pairs = InstallReferrerParser.Parse("utm_source%3Dposter%26dl_cid%3Dabc");

        Assert.Equal("poster", pairs["utm_source"]);
        Assert.Equal("abc", pairs["dl_cid"]);
    }

    [Fact]
    public void TryGetClickId_DoubleEncodedReferrer_StillFindsTheClickId()
    {
        Assert.True(InstallReferrerParser.TryGetClickId("dl_cid%3D01JQ8Z0K7M8T9RV%26utm_source%3Dposter", out string clickId));
        Assert.Equal("01JQ8Z0K7M8T9RV", clickId);
    }

    [Fact]
    public void Parse_EncodedValue_IsDecoded()
    {
        Assert.Equal("spring sale", InstallReferrerParser.Parse("utm_campaign=spring%20sale")["utm_campaign"]);
    }

    [Fact]
    public void Parse_MalformedEscapeSequence_LeavesTheTextAsItWas()
    {
        // Explicit default: a referrer that cannot be decoded is simply one that will not match.
        IReadOnlyDictionary<string, string> pairs = InstallReferrerParser.Parse("dl_cid=abc%zz");

        Assert.Equal("abc%zz", pairs["dl_cid"]);
    }

    // ================================================================ caps

    [Fact]
    public void Parse_MoreThanMaxPairs_StopsAtTheCap()
    {
        var builder = new StringBuilder();
        for (int i = 0; i < InstallReferrerParser.MaxPairs + 20; i++)
        {
            if (i > 0)
            {
                builder.Append('&');
            }

            builder.Append(CultureInfo.InvariantCulture, $"k{i}=v{i}");
        }

        IReadOnlyDictionary<string, string> pairs = InstallReferrerParser.Parse(builder.ToString());

        Assert.Equal(InstallReferrerParser.MaxPairs, pairs.Count);
        Assert.Equal("v0", pairs["k0"]);
    }

    [Fact]
    public void Parse_ReferrerLongerThanTheLimit_IgnoresEverythingPastIt()
    {
        // A referrer padded past the cap must not be able to smuggle a pair in, and the pair that the
        // truncation cut in half must be dropped rather than returned as a prefix of the real value.
        string referrer = "dl_cid=01JQ8Z0K7M8T9RV&big=" + new string('v', 1100);

        Assert.True(referrer.Length > InstallReferrerParser.MaxReferrerLength);

        IReadOnlyDictionary<string, string> pairs = InstallReferrerParser.Parse(referrer);

        Assert.Equal("01JQ8Z0K7M8T9RV", Assert.Single(pairs).Value);
        Assert.False(pairs.ContainsKey("big"));
    }

    [Fact]
    public void Parse_ReferrerExactlyAtTheLimit_IsParsedInFull()
    {
        const string Prefix = "dl_cid=abc&pad=";
        string referrer = Prefix + new string('v', InstallReferrerParser.MaxReferrerLength - Prefix.Length);

        Assert.Equal(InstallReferrerParser.MaxReferrerLength, referrer.Length);

        IReadOnlyDictionary<string, string> pairs = InstallReferrerParser.Parse(referrer);

        Assert.Equal(2, pairs.Count);
        Assert.Equal("abc", pairs["dl_cid"]);
    }

    [Fact]
    public void Constants_MatchTheBindingContract()
    {
        Assert.Equal("dl_cid", InstallReferrerParser.ClickIdKey);
        Assert.Equal(1024, InstallReferrerParser.MaxReferrerLength);
    }
}
