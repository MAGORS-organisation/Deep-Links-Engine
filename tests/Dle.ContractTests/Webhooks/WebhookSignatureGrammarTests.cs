using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Dle.Control.Features.Webhooks;

namespace Dle.ContractTests.Webhooks;

/// <summary>
/// The <c>DLE-Signature</c> and <c>DLE-Alg</c> header grammar of §B.7.4.
/// </summary>
/// <remarks>
/// <para>
/// §B.7.4 prints the header as four members — <c>t=</c>, <c>v1=</c>, <c>v2=</c> and <c>kid=</c> — with
/// <c>DLE-Alg: HS256+Ed25519</c> alongside. Every receiver in every language parses that by splitting
/// on commas and on the first equals sign, so the member names, their separators and the encoding of
/// their values are as much a wire contract as any JSON property.
/// </para>
/// <para>
/// The forward-compatibility clause is asserted as well, because it is the whole reason the header has
/// this shape. §E.5.3 plans a third slot, <c>v3=&lt;ML-DSA-65&gt;</c>. That is only a non-breaking
/// change if today's parser already ignores members it does not recognise — so a test that an unknown
/// member is tolerated is a test of the post-quantum migration path, not a test of a parser detail.
/// </para>
/// </remarks>
public sealed class WebhookSignatureGrammarTests
{
    /// <summary>
    /// The grammar of §B.7.4, as a receiver would implement it.
    /// </summary>
    /// <remarks>
    /// Written independently of <see cref="WebhookSignature"/> on purpose. Parsing the product's
    /// output with the product's own parser would pass for any header the product can emit, including
    /// one no third-party receiver could read.
    /// </remarks>
    private static readonly Regex Grammar = new(
        @"^t=(?<t>[0-9]{1,19})"
        + @"(?:,\s*v1=(?<v1>[A-Za-z0-9+/]+={0,2}))?"
        + @"(?:,\s*v2=(?<v2>[A-Za-z0-9+/]+={0,2}))?"
        + @"(?:,\s*kid=(?<kid>[A-Za-z0-9_.:-]{1,64}))?$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    private static readonly DateTimeOffset Moment = new(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);

    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("a-webhook-subscription-secret-0001");

    private static readonly byte[] Body =
        Encoding.UTF8.GetBytes("""{"event":"webhook.test","id":"1","occurred_at":"2026-09-03T10:00:00+00:00","data":{}}""");

    [Fact]
    [Trait("Contract", "B.7.4")]
    public void SignatureHeader_MatchesThePublishedGrammar()
    {
        string header = WebhookSignature.Create(Body, Secret, signer: null, Moment);

        Match match = Grammar.Match(header);

        Assert.True(match.Success, "The produced header does not match the §B.7.4 grammar: " + header);
        Assert.Equal(Moment.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), match.Groups["t"].Value);
        Assert.True(match.Groups["v1"].Success, "The v1 slot is mandatory: it is the one every receiver starts with.");
    }

    [Fact]
    [Trait("Contract", "B.7.4")]
    public void SignatureHeader_TimestampIsUnixSecondsNotMilliseconds()
    {
        // §B.7.4 prints t=1756900000, which is seconds. A receiver comparing a millisecond value
        // against its own clock would reject every delivery as stale by fifty thousand years.
        string header = WebhookSignature.Create(Body, Secret, signer: null, Moment);

        long stamp = long.Parse(Grammar.Match(header).Groups["t"].Value, CultureInfo.InvariantCulture);

        Assert.Equal(Moment.ToUnixTimeSeconds(), stamp);
    }

    [Fact]
    [Trait("Contract", "B.7.4")]
    public void SignatureHeader_V1IsBase64HmacSha256OverTimestampDotBody()
    {
        // The signing input is published as t + "." + body. Recomputing it here, from the
        // specification rather than from the implementation, is what makes this a contract test: a
        // receiver written against the documentation must arrive at the same bytes.
        string header = WebhookSignature.Create(Body, Secret, signer: null, Moment);

        byte[] signingInput =
        [
            .. Encoding.ASCII.GetBytes(Moment.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            (byte)'.',
            .. Body,
        ];

        string expected = Convert.ToBase64String(HMACSHA256.HashData(Secret, signingInput));

        Assert.Equal(expected, Grammar.Match(header).Groups["v1"].Value);
    }

    [Fact]
    [Trait("Contract", "B.7.4")]
    public void AlgorithmHeader_NamesBothSlots()
    {
        Assert.Equal("HS256+Ed25519", WebhookSignature.AlgorithmValue);
        Assert.Equal("DLE-Signature", WebhookSignature.SignatureHeader);
        Assert.Equal("DLE-Alg", WebhookSignature.AlgorithmHeader);
    }

    [Theory]
    [Trait("Contract", "B.7.4")]
    [InlineData("t=1756900000, v1=Zm9v", true)]
    [InlineData("t=1756900000, v1=Zm9v, v2=YmFy, kid=k_2026_09", true)]
    [InlineData("t=1756900000, v1=Zm9v, v2=YmFy, v3=YmF6, kid=k_2026_09", true)]
    [InlineData("t=1756900000", false)]
    [InlineData("v1=Zm9v", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Parser_AcceptsAHeaderWithATimestampAndAtLeastOneSlot(string? header, bool expected)
    {
        // The v3 case is the forward-compatibility clause of §E.5.3: an unknown member must be
        // ignored, not refused, or adding a post-quantum signature later breaks every receiver.
        Assert.Equal(expected, WebhookSignature.TryParse(header, out _));
    }

    [Fact]
    [Trait("Contract", "B.7.4")]
    public void Parser_IgnoresAnUnknownMemberWithoutLosingTheKnownOnes()
    {
        const string header = "t=1756900000, v1=Zm9v, v3=YmF6, kid=k_2026_09";

        Assert.True(WebhookSignature.TryParse(header, out WebhookSignatureHeader parsed));

        Assert.Equal(1756900000L, parsed.UnixSeconds);
        Assert.Equal("k_2026_09", parsed.Kid);
        Assert.NotNull(parsed.V1);
        Assert.Null(parsed.V2);
    }

    [Fact]
    [Trait("Contract", "B.7.4")]
    public void Parser_IsIndifferentToWhitespaceAndMemberOrder()
    {
        // §B.7.4 prints the header wrapped over four lines, so a receiver will see the members in any
        // order and with arbitrary spacing after each comma.
        Assert.True(WebhookSignature.TryParse("kid=k_2026_09,v1=Zm9v,t=1756900000", out WebhookSignatureHeader compact));
        Assert.True(WebhookSignature.TryParse("t=1756900000,   v1=Zm9v ,  kid=k_2026_09", out WebhookSignatureHeader spaced));

        Assert.Equal(compact.UnixSeconds, spaced.UnixSeconds);
        Assert.Equal(compact.Kid, spaced.Kid);
    }
}
