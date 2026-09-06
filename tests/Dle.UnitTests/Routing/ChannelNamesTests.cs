using Dle.Domain.Clients;
using Dle.Domain.Routing;
using Xunit;

namespace Dle.UnitTests.Routing;

/// <summary>
/// The channel names are a wire contract: they appear inside customer-authored routing rules and in
/// the "channel" column of click_events. Renaming one would silently retarget every stored rule and
/// orphan every historical report, so the exact spellings are pinned here.
/// </summary>
public sealed class ChannelNamesTests
{
    [Theory]
    [InlineData(ClientChannel.Unknown, "unknown")]
    [InlineData(ClientChannel.Browser, "browser")]
    [InlineData(ClientChannel.Crawler, "crawler")]
    [InlineData(ClientChannel.InAppFacebook, "in_app_fb")]
    [InlineData(ClientChannel.InAppInstagram, "in_app_ig")]
    [InlineData(ClientChannel.InAppTikTok, "in_app_tiktok")]
    [InlineData(ClientChannel.InAppLinkedIn, "in_app_linkedin")]
    [InlineData(ClientChannel.InAppSnapchat, "in_app_snapchat")]
    [InlineData(ClientChannel.InAppTwitter, "in_app_x")]
    [InlineData(ClientChannel.InAppWhatsApp, "in_app_whatsapp")]
    [InlineData(ClientChannel.InAppTelegram, "in_app_telegram")]
    [InlineData(ClientChannel.InAppPinterest, "in_app_pinterest")]
    [InlineData(ClientChannel.InAppGeneric, "in_app_other")]
    [InlineData(ClientChannel.NativeApp, "app")]
    public void From_ProducesTheExactWireName(ClientChannel channel, string expected)
    {
        Assert.Equal(expected, ChannelNames.From(channel));
    }

    [Fact]
    public void From_UndefinedEnumValue_FallsBackToUnknown()
    {
        Assert.Equal(ChannelNames.Unknown, ChannelNames.From((ClientChannel)99));
    }

    [Fact]
    public void FromThenParse_RoundTripsEveryDeclaredChannel()
    {
        foreach (ClientChannel channel in Enum.GetValues<ClientChannel>())
        {
            Assert.Equal(channel, ChannelNames.Parse(ChannelNames.From(channel)));
        }
    }

    [Theory]
    [InlineData("IN_APP_FB", ClientChannel.InAppFacebook)]
    [InlineData("  browser  ", ClientChannel.Browser)]
    [InlineData("In_App_X", ClientChannel.InAppTwitter)]
    public void Parse_IsTolerantOfCaseAndSurroundingWhitespace(string name, ClientChannel expected)
    {
        Assert.Equal(expected, ChannelNames.Parse(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("in_app_myspace")]
    [InlineData("facebook")]
    public void Parse_UnrecognisedName_IsUnknownRatherThanAnException(string? name)
    {
        Assert.Equal(ClientChannel.Unknown, ChannelNames.Parse(name));
    }

    [Theory]
    [InlineData(ClientChannel.InAppFacebook, true)]
    [InlineData(ClientChannel.InAppInstagram, true)]
    [InlineData(ClientChannel.InAppTikTok, true)]
    [InlineData(ClientChannel.InAppLinkedIn, true)]
    [InlineData(ClientChannel.InAppSnapchat, true)]
    [InlineData(ClientChannel.InAppTwitter, true)]
    [InlineData(ClientChannel.InAppWhatsApp, true)]
    [InlineData(ClientChannel.InAppTelegram, true)]
    [InlineData(ClientChannel.InAppPinterest, true)]
    [InlineData(ClientChannel.InAppGeneric, true)]
    [InlineData(ClientChannel.Browser, false)]
    [InlineData(ClientChannel.Crawler, false)]
    [InlineData(ClientChannel.NativeApp, false)]
    [InlineData(ClientChannel.Unknown, false)]
    public void IsInAppWebView_CoversExactlyTheEmbeddedBrowsers(ClientChannel channel, bool expected)
    {
        Assert.Equal(expected, ChannelNames.IsInAppWebView(channel));
    }
}
