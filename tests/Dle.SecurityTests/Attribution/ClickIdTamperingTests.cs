using CsCheck;

using Dle.Control.Features.Attribution;
using Dle.Crypto;
using Dle.Domain.Attribution;
namespace Dle.SecurityTests.Attribution;

/// <summary>
/// T-05 / TC-167: a click identifier edited in a Play referrer is never matched, and is recorded as
/// tampered.
/// </summary>
/// <remarks>
/// <para>
/// The Play install referrer is a string an application on the device hands us, and §E.2.2 T-05 is
/// specific about the consequence of trusting it: "skreslenie výplat partnerom". A partner who can
/// edit <c>dl_cid</c> can attribute an organic install to their own campaign and get paid for it. The
/// mitigation named there is that the identifier is "podpísaný krátky token (HMAC), nie surové ID",
/// which is what makes an edit detectable rather than merely unlikely.
/// </para>
/// <para>
/// What is asserted here is the whole of that property at its source: <see cref="ClickIdCodec"/>
/// refuses to decode anything it did not mint, so the strategy in <c>ResolveInstall</c> that asks it
/// takes the tampered branch and returns no match. The end-to-end half — that the resulting
/// attribution row carries <c>tampered=true</c> in its evidence — needs a database and lives in the
/// integration suite; the constants it writes are asserted here so the two cannot drift apart
/// silently.
/// </para>
/// </remarks>
public sealed class ClickIdTamperingTests
{
    /// <summary>The alphabet a click identifier is written in.</summary>
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    private static readonly DateTimeOffset Minted = new(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);

    private static readonly byte[] PermutationKey =
        [.. Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + 1))];

    private static readonly byte[] MacKey =
        [.. Enumerable.Range(0, 32).Select(i => (byte)(i * 11 + 3))];

    private static readonly ClickIdCodec Codec = new(PermutationKey, MacKey);

    [Fact]
    [Trait("TestCase", "TC-167")]
    public void ClickId_AsMinted_DecodesBackToItsInstant()
    {
        // The control: an untouched identifier decodes, which is what makes the refusals below mean
        // something.
        string clickId = Codec.New(Minted);

        Assert.True(Codec.TryDecode(clickId, out DateTimeOffset occurredAt, out _));
        Assert.True(
            (occurredAt - Minted).Duration() < TimeSpan.FromSeconds(2),
            "The decoded instant must be the one the identifier was minted at.");
    }

    [Fact]
    [Trait("TestCase", "TC-167")]
    [Trait("Threat", "T-05")]
    public void ClickId_WithAnyOneCharacterChanged_FailsToDecode()
    {
        // Every position, every replacement character. This is the mutation a partner actually makes:
        // one digit, to point the install at a different click. There is no position where it works.
        string clickId = Codec.New(Minted);

        List<string> accepted = [];

        for (int position = 0; position < clickId.Length; position++)
        {
            foreach (char replacement in Alphabet)
            {
                if (replacement == clickId[position])
                {
                    continue;
                }

                char[] mutated = clickId.ToCharArray();
                mutated[position] = replacement;

                string candidate = new(mutated);

                if (Codec.TryDecode(candidate, out _, out _))
                {
                    accepted.Add(candidate);
                }
            }
        }

        Assert.True(
            accepted.Count == 0,
            "A mutated click identifier decoded successfully, so a partner can forge attributions: "
                + string.Join(", ", accepted.Take(10)));
    }

    [Fact]
    [Trait("TestCase", "TC-167")]
    [Trait("Threat", "T-05")]
    public void ClickId_MintedUnderAnotherDeploymentsKey_FailsToDecode()
    {
        // Two instances of the engine must not accept each other's identifiers. Otherwise a partner
        // who runs their own copy mints whatever they like and presents it here.
        ClickIdCodec other = new(MacKey, PermutationKey);

        Assert.False(Codec.TryDecode(other.New(Minted), out _, out _));
    }

    [Theory]
    [Trait("TestCase", "TC-167")]
    [Trait("Threat", "T-05")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("aB3xK9pQ")]
    [InlineData("0000000000000000000000000000")]
    [InlineData("../../etc/passwd")]
    [InlineData("' OR 1=1 --")]
    [InlineData("\u0000\u0001\u0002")]
    public void ClickId_ThatWasNeverMinted_FailsToDecode(string candidate)
    {
        Assert.False(Codec.TryDecode(candidate, out _, out _));
    }

    [Fact]
    [Trait("TestCase", "TC-167")]
    [Trait("Threat", "T-05")]
    public void ClickId_DecodeIsTotalOverArbitraryText()
    {
        // The decoder reads a value from an untrusted device, so it must terminate and answer for
        // anything at all — including text that is not in its alphabet, is the wrong length, or is not
        // text a person would type.
        Gen.String.Sample(
            candidate =>
            {
                _ = Codec.TryDecode(candidate, out _, out _);
                return true;
            },
            iter: 20_000);
    }

    [Fact]
    [Trait("TestCase", "TC-167")]
    [Trait("Threat", "T-05")]
    public void ClickId_NoArbitraryStringIsEverAccepted()
    {
        // The complement of the mutation test: not only does editing a real identifier fail, but
        // guessing one does too. Twenty thousand strings from the identifier's own alphabet and
        // length, none of which the codec minted.
        Gen.Char[Alphabet].Array[ClickIdCodec.Length].Sample(
            characters => !Codec.TryDecode(new string(characters), out _, out _),
            iter: 20_000);
    }

    [Fact]
    [Trait("TestCase", "TC-167")]
    public void InstallReferrer_CarriesTheClickIdUnderTheDocumentedKey()
    {
        // §B.7.2 shows the referrer as dl_cid%3D…%26utm_source%3Dfb, which is the percent-encoded form
        // Play delivers. The parser has to see through that, or every Android attribution silently
        // becomes organic.
        string clickId = Codec.New(Minted);
        string referrer = "dl_cid%3D" + clickId + "%26utm_source%3Dfb";

        Assert.True(InstallReferrerParser.TryGetClickId(referrer, out string parsed));
        Assert.Equal(clickId, parsed);
        Assert.True(Codec.TryDecode(parsed, out _, out _));
    }

    [Fact]
    [Trait("TestCase", "TC-167")]
    [Trait("Threat", "T-05")]
    public void InstallReferrer_WithATamperedClickId_ParsesButDoesNotDecode()
    {
        // Both halves in one place, because this is the exact sequence ResolveInstall performs: the
        // parser extracts whatever the device sent, and the codec is what decides whether it is ours.
        // A design where the parser did the rejecting would be a design where any well-formed string
        // is an attribution.
        string clickId = Codec.New(Minted);
        string tampered = clickId[..^1] + (clickId[^1] == 'A' ? 'B' : 'A');

        string referrer = "dl_cid%3D" + tampered + "%26utm_source%3Dfb";

        Assert.True(InstallReferrerParser.TryGetClickId(referrer, out string parsed));
        Assert.Equal(tampered, parsed);
        Assert.False(Codec.TryDecode(parsed, out _, out _));
    }

    [Fact]
    [Trait("TestCase", "TC-142")]
    public void InstallReferrer_WithoutAClickId_IsAnOrganicInstallRatherThanAnError()
    {
        // TC-142: an organic install carries no dl_cid, and that is not a failure. Conflating the two
        // would turn every Play Store install into a logged security event.
        Assert.False(InstallReferrerParser.TryGetClickId(
            "utm_source=google-play&utm_medium=organic",
            out _));
    }

    [Fact]
    [Trait("TestCase", "TC-167")]
    public void TamperedEvidence_UsesTheConstantsTheResolverWrites()
    {
        // The end-to-end assertion that the attribution row says `tampered` lives in the integration
        // suite, which needs a database. What can be pinned here is the vocabulary: if a rename moved
        // the key or the reason, the integration test would still pass against the new spelling while
        // every consumer of the evidence column broke.
        Assert.Equal("tampered", AttributionEvidence.TamperedKey);
        Assert.Equal("tampered_click_id", AttributionReasons.TamperedClickId);
    }

    [Fact]
    [Trait("TestCase", "TC-167")]
    public void ClickId_IsFixedWidth_SoItsLengthLeaksNothing()
    {
        // A variable-width identifier would leak the sequence number's magnitude, and with it the
        // instance's click volume, to anybody holding two identifiers.
        Gen.Int[0, 400_000].Sample(
            seconds => Codec.New(Minted.AddSeconds(seconds)).Length == ClickIdCodec.Length,
            iter: 5_000);
    }
}
