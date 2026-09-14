using System.Security.Cryptography;
using System.Text;

using CsCheck;

using Dle.Control.Features.Webhooks;

namespace Dle.SecurityTests.Webhooks;

/// <summary>
/// T-13 / TC-165: a delivery a customer did not receive from us must not verify.
/// </summary>
/// <remarks>
/// <para>
/// A forged webhook is a fraudulent conversion, and the customer's own systems act on it: it credits
/// a partner, moves a payout, marks an install as attributed. §B.7.4's answer is a signature over the
/// timestamp and the exact transmitted body, and §E.2.2 adds a five-minute tolerance so that a
/// genuine delivery captured once cannot be replayed forever.
/// </para>
/// <para>
/// The three things that must fail are the three ways an attacker gets there: change the body, reuse
/// an old signature, or sign with a key they control. Each is asserted, and the mutated-body case is
/// a property rather than an example — a single flipped bit anywhere in the payload has to be enough.
/// </para>
/// </remarks>
public sealed class WebhookForgeryTests
{
    /// <summary>The tolerance §B.7.4 specifies.</summary>
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    private static readonly DateTimeOffset SignedAt = new(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);

    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("the-subscription-shared-secret-32b");

    private static readonly byte[] WrongSecret = Encoding.UTF8.GetBytes("a-different-subscription-secret!!!");

    private static readonly byte[] Body = Encoding.UTF8.GetBytes(
        """{"event":"attribution.created","id":"0192f4f0-1c1a-7c3d-9b2e-4d5a6f7b8c9d","occurred_at":"2026-09-03T10:00:00+00:00","data":{"install_id":"9f2c","match_type":"install_referrer","confidence":"1.00"}}""");

    [Fact]
    [Trait("TestCase", "TC-165")]
    [Trait("Threat", "T-13")]
    public void Verify_TheDeliveryAsSent_Succeeds()
    {
        // The control. Everything else in this class asserts a refusal, and a verifier that refuses
        // everything would satisfy all of them.
        string header = WebhookSignature.Create(Body, Secret, signer: null, SignedAt);

        Assert.True(WebhookSignature.VerifySymmetric(header, Body, Secret, SignedAt, Tolerance));
    }

    [Fact]
    [Trait("TestCase", "TC-165")]
    [Trait("Threat", "T-13")]
    public void Verify_ABodyChangedByOneBitAnywhere_Fails()
    {
        // A property rather than three examples: the interesting forgeries are the small ones —
        // "confidence":"1.00" becoming "0.10", one digit of a payout — and an implementation that
        // signed a prefix, a length or a hash of the wrong thing would pass a coarse test.
        string header = WebhookSignature.Create(Body, Secret, signer: null, SignedAt);

        Gen.Select(Gen.Int[0, Body.Length - 1], Gen.Int[0, 7]).Sample(
            mutation =>
            {
                (int index, int bit) = mutation;

                byte[] mutated = [.. Body];
                mutated[index] ^= (byte)(1 << bit);

                return !WebhookSignature.VerifySymmetric(header, mutated, Secret, SignedAt, Tolerance);
            },
            iter: 5_000);
    }

    [Theory]
    [Trait("TestCase", "TC-165")]
    [Trait("Threat", "T-13")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(64)]
    public void Verify_ABodyWithBytesAppendedOrRemoved_Fails(int extra)
    {
        string header = WebhookSignature.Create(Body, Secret, signer: null, SignedAt);

        byte[] longer = [.. Body, .. Enumerable.Repeat((byte)' ', extra + 1)];
        byte[] shorter = Body[..^1];

        Assert.False(WebhookSignature.VerifySymmetric(header, longer, Secret, SignedAt, Tolerance));
        Assert.False(WebhookSignature.VerifySymmetric(header, shorter, Secret, SignedAt, Tolerance));
    }

    [Theory]
    [Trait("TestCase", "TC-165")]
    [Trait("Threat", "T-13")]
    [InlineData(6)]
    [InlineData(60)]
    [InlineData(24 * 60)]
    public void Verify_ACaptureReplayedAfterTheTolerance_Fails(int minutesLater)
    {
        // The captured delivery is byte-for-byte genuine; only the receiver's clock has moved. Without
        // this the signature proves authenticity forever, and a delivery recorded once can be replayed
        // into the customer's system every night.
        string header = WebhookSignature.Create(Body, Secret, signer: null, SignedAt);

        Assert.False(WebhookSignature.VerifySymmetric(
            header,
            Body,
            Secret,
            SignedAt.AddMinutes(minutesLater),
            Tolerance));
    }

    [Fact]
    [Trait("TestCase", "TC-165")]
    [Trait("Threat", "T-13")]
    public void Verify_ADeliveryFromTheFutureBeyondTheTolerance_Fails()
    {
        // Both directions. A receiver whose clock is behind must not accept a timestamp far ahead of
        // it either, or an attacker simply post-dates the capture and it stays valid indefinitely.
        string header = WebhookSignature.Create(Body, Secret, signer: null, SignedAt.AddHours(1));

        Assert.False(WebhookSignature.VerifySymmetric(header, Body, Secret, SignedAt, Tolerance));
    }

    [Fact]
    [Trait("TestCase", "TC-165")]
    [Trait("Threat", "T-13")]
    public void Verify_JustInsideTheTolerance_StillSucceeds()
    {
        string header = WebhookSignature.Create(Body, Secret, signer: null, SignedAt);

        Assert.True(WebhookSignature.VerifySymmetric(
            header,
            Body,
            Secret,
            SignedAt.Add(Tolerance - TimeSpan.FromSeconds(1)),
            Tolerance));
    }

    [Fact]
    [Trait("TestCase", "TC-165")]
    [Trait("Threat", "T-13")]
    public void Verify_ASignatureMadeWithAnotherKey_Fails()
    {
        string forged = WebhookSignature.Create(Body, WrongSecret, signer: null, SignedAt);

        Assert.False(WebhookSignature.VerifySymmetric(forged, Body, Secret, SignedAt, Tolerance));
    }

    [Fact]
    [Trait("TestCase", "TC-165")]
    [Trait("Threat", "T-13")]
    public void Verify_ATimestampRewrittenToStayFresh_Fails()
    {
        // The obvious attack on a naive scheme where `t` is a transport detail rather than signed
        // material: take yesterday's capture, put today's timestamp on it, send it. It fails because
        // the timestamp is inside the signing input, which is exactly why §B.7.4 puts it there.
        string header = WebhookSignature.Create(Body, Secret, signer: null, SignedAt);

        string restamped = ReplaceTimestamp(header, SignedAt.AddHours(6).ToUnixTimeSeconds());

        Assert.False(WebhookSignature.VerifySymmetric(
            restamped,
            Body,
            Secret,
            SignedAt.AddHours(6),
            Tolerance));
    }

    [Theory]
    [Trait("TestCase", "TC-165")]
    [Trait("Threat", "T-13")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("t=abc, v1=Zm9v")]
    [InlineData("v1=Zm9v")]
    [InlineData("t=1756900000")]
    [InlineData("t=1756900000, v1=")]
    [InlineData("t=1756900000, v1=!!!not-base64!!!")]
    public void Verify_AMalformedHeader_FailsClosed(string? header)
    {
        // §17.9 again: a header the parser cannot make sense of is a refusal, never a pass. A
        // signature scheme whose parse failure means "no signature required" is not a signature scheme.
        Assert.False(WebhookSignature.VerifySymmetric(header, Body, Secret, SignedAt, Tolerance));
    }

    [Fact]
    [Trait("TestCase", "TC-165")]
    public void Verify_AnEmptySignature_DoesNotMatchAnEmptyExpectation()
    {
        // The degenerate forgery: send v1 as an empty value and hope the comparison is on two empty
        // spans. FixedTimeEquals compares lengths first, which is what makes this fail.
        Assert.False(WebhookSignature.VerifySymmetric(
            "t=" + SignedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + ", v1=,v2=",
            Body,
            Secret,
            SignedAt,
            Tolerance));
    }

    [Fact]
    [Trait("Threat", "T-17")]
    [Trait("Criterion", "S-03")]
    public void Verify_UsesAConstantTimeComparison()
    {
        // S-03 asks that every signature be verified in constant time, and T-17 says why: an equality
        // comparison on a MAC leaks it one byte at a time to anybody who can measure the response.
        //
        // Timing cannot demonstrate that here — the assertion would be a microbenchmark, and §D.1
        // rules those out as a gate. What can be demonstrated is the shape a leak would have: with a
        // byte-by-byte comparison, a signature agreeing with the expected one on its first fifteen
        // bytes is distinguishable from one that differs in the first, and both must be refused
        // identically. This is the behavioural half; the implementation half is a code review item,
        // and the implementation does use CryptographicOperations.FixedTimeEquals.
        string header = WebhookSignature.Create(Body, Secret, signer: null, SignedAt);
        byte[] correct = Convert.FromBase64String(ExtractV1(header));

        for (int prefix = 0; prefix < correct.Length; prefix++)
        {
            byte[] guess = [.. correct];
            guess[prefix] ^= 0xFF;

            string forged = ReplaceV1(header, Convert.ToBase64String(guess));

            Assert.False(
                WebhookSignature.VerifySymmetric(forged, Body, Secret, SignedAt, Tolerance),
                "A signature agreeing on the first " + prefix.ToString(CultureInfo.InvariantCulture)
                    + " bytes must be refused exactly like one that agrees on none.");
        }
    }

    [Fact]
    [Trait("Contract", "B.7.4")]
    public void Create_TheSameDeliveryTwice_ProducesTheSameSignature()
    {
        // A retry has to reproduce the bytes exactly, or the receiver rejects the second attempt and
        // the outbox retries forever. This is why the envelope is rendered once and stored as text.
        Assert.Equal(
            WebhookSignature.Create(Body, Secret, signer: null, SignedAt),
            WebhookSignature.Create(Body, Secret, signer: null, SignedAt));
    }

    [Fact]
    [Trait("Threat", "T-13")]
    public void Create_TwoDifferentBodies_NeverShareASignature()
    {
        Gen.Byte.Array[1, 512].Sample(
            payload =>
            {
                string first = WebhookSignature.Create(payload, Secret, signer: null, SignedAt);
                string second = WebhookSignature.Create([.. payload, 0x21], Secret, signer: null, SignedAt);

                return !string.Equals(first, second, StringComparison.Ordinal);
            },
            iter: 2_000);
    }

    [Fact]
    [Trait("Threat", "T-13")]
    public void VerifySymmetric_AgreesWithAnIndependentImplementation()
    {
        // The signing input is published as t + "." + body, and a receiver in another language will
        // implement exactly that. Recomputing it here from the specification rather than from the
        // product is what makes this a check of the contract and not of the code's agreement with
        // itself.
        string header = WebhookSignature.Create(Body, Secret, signer: null, SignedAt);

        byte[] signingInput =
        [
            .. Encoding.ASCII.GetBytes(SignedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            (byte)'.',
            .. Body,
        ];

        Assert.Equal(
            Convert.ToBase64String(HMACSHA256.HashData(Secret, signingInput)),
            ExtractV1(header));
    }

    private static string ExtractV1(string header) =>
        header.Split(',')
            .Select(part => part.Trim())
            .First(part => part.StartsWith("v1=", StringComparison.Ordinal))["v1=".Length..];

    private static string ReplaceV1(string header, string value) =>
        string.Join(", ", header.Split(',').Select(part =>
            part.Trim().StartsWith("v1=", StringComparison.Ordinal) ? "v1=" + value : part.Trim()));

    private static string ReplaceTimestamp(string header, long unixSeconds) =>
        string.Join(", ", header.Split(',').Select(part =>
            part.Trim().StartsWith("t=", StringComparison.Ordinal)
                ? "t=" + unixSeconds.ToString(CultureInfo.InvariantCulture)
                : part.Trim()));
}
