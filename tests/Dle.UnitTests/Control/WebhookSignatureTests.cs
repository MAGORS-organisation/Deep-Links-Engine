using System.Security.Cryptography;
using System.Text;

using Dle.Control.Features.Webhooks;
using Dle.Crypto;
using Dle.Domain.Crypto;

using Xunit;

namespace Dle.UnitTests.Control;

/// <summary>
/// A webhook is the only place where the engine speaks to a system it does not control, so the
/// receiver's only defence is the signature (§B.7.4, TC-165). Two signatures travel together:
/// <c>v1</c>, a shared-secret HMAC every integrator can check in three lines, and <c>v2</c>, an
/// Ed25519 signature over the same bytes that can be checked against the published JWKS without
/// sharing a secret at all.
/// </summary>
public sealed class WebhookSignatureTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 20, 14, 30, 0, TimeSpan.Zero);

    private static readonly byte[] Secret = "webhook-shared-secret-0123456789ab"u8.ToArray();

    private static readonly byte[] Body =
        """{"event":"attribution.created","id":"1","data":{"match_type":"install_referrer"}}"""u8.ToArray();

    [Fact]
    public void Create_ProducesTheDocumentedHeaderShape()
    {
        (ISigner signer, _) = KeyPair();

        string header = WebhookSignature.Create(Body, Secret, signer, Now);

        Assert.StartsWith("t=" + Now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), header, StringComparison.Ordinal);
        Assert.Contains(", v1=", header, StringComparison.Ordinal);
        Assert.Contains(", v2=", header, StringComparison.Ordinal);
        Assert.Contains(", kid=" + signer.KeyId, header, StringComparison.Ordinal);

        Assert.True(WebhookSignature.TryParse(header, out WebhookSignatureHeader parsed));

        Assert.Equal(Now.ToUnixTimeSeconds(), parsed.UnixSeconds);
        Assert.NotNull(parsed.V1);
        Assert.NotNull(parsed.V2);
        Assert.Equal(signer.KeyId, parsed.Kid);
        Assert.Equal(SHA256.HashSizeInBytes, parsed.V1!.Length);
        Assert.Equal(Ed25519Signer.SignatureSize, parsed.V2!.Length);
    }

    [Fact]
    public void Create_WithoutAnAsymmetricSigner_StillCarriesTheSharedSecretSignature()
    {
        string header = WebhookSignature.Create(Body, Secret, signer: null, Now);

        Assert.True(WebhookSignature.TryParse(header, out WebhookSignatureHeader parsed));

        Assert.NotNull(parsed.V1);
        Assert.Null(parsed.V2);
        Assert.Null(parsed.Kid);
        Assert.True(WebhookSignature.VerifySymmetric(header, Body, Secret, Now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void VerifySymmetric_TheBodyThatWasSigned_Passes()
    {
        string header = WebhookSignature.Create(Body, Secret, signer: null, Now);

        Assert.True(WebhookSignature.VerifySymmetric(header, Body, Secret, Now, TimeSpan.FromMinutes(5)));
    }

    /// <summary>
    /// TC-165. One byte of the body is changed. Both signatures have to fail, because either one on
    /// its own is what an integrator will rely on.
    /// </summary>
    [Fact]
    [Trait("TestCase", "TC-165")]
    public void Verify_BodyWithOneByteChanged_Fails()
    {
        (ISigner signer, VerificationKey key) = KeyPair();

        string header = WebhookSignature.Create(Body, Secret, signer, Now);

        for (int position = 0; position < Body.Length; position += 7)
        {
            byte[] tampered = [.. Body];
            tampered[position] ^= 0x01;

            Assert.False(
                WebhookSignature.VerifySymmetric(header, tampered, Secret, Now, TimeSpan.FromMinutes(5)),
                $"v1 accepted a body mutated at byte {position}");

            Assert.False(
                VerifyAsymmetric(header, tampered, key),
                $"v2 accepted a body mutated at byte {position}");
        }
    }

    [Fact]
    [Trait("TestCase", "TC-165")]
    public void Verify_BodyWithABytesAppended_Fails()
    {
        (ISigner signer, VerificationKey key) = KeyPair();

        string header = WebhookSignature.Create(Body, Secret, signer, Now);

        byte[] extended = [.. Body, (byte)' '];

        Assert.False(WebhookSignature.VerifySymmetric(header, extended, Secret, Now, TimeSpan.FromMinutes(5)));
        Assert.False(VerifyAsymmetric(header, extended, key));
    }

    /// <summary>
    /// The signed input is the timestamp, a full stop and the body — so moving the timestamp while
    /// keeping the signature fails, which is what stops a captured delivery from being replayed with
    /// a fresh clock.
    /// </summary>
    [Fact]
    [Trait("TestCase", "TC-165")]
    public void Verify_TimestampRewritten_Fails()
    {
        string header = WebhookSignature.Create(Body, Secret, signer: null, Now);

        string rewritten = header.Replace(
            "t=" + Now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            "t=" + Now.AddMinutes(1).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

        Assert.False(WebhookSignature.VerifySymmetric(rewritten, Body, Secret, Now.AddMinutes(1), TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void VerifySymmetric_TheWrongSecret_Fails()
    {
        string header = WebhookSignature.Create(Body, Secret, signer: null, Now);

        Assert.False(WebhookSignature.VerifySymmetric(
            header,
            Body,
            "another-webhook-secret-0123456789a"u8.ToArray(),
            Now,
            TimeSpan.FromMinutes(5)));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(4, true)]
    [InlineData(6, false)]
    [InlineData(-4, true)]
    [InlineData(-6, false)]
    public void VerifySymmetric_HonoursTheTimestampTolerance(int minutesOfSkew, bool expected)
    {
        string header = WebhookSignature.Create(Body, Secret, signer: null, Now);

        // The tolerance is symmetric: a delivery from the future is as suspect as an old one, because
        // a receiver whose clock is behind must not be a replay window.
        Assert.Equal(
            expected,
            WebhookSignature.VerifySymmetric(header, Body, Secret, Now.AddMinutes(minutesOfSkew), TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Create_TwoDeliveriesOfOneBody_ProduceTheSameSignatureAtTheSameInstant()
    {
        string first = WebhookSignature.Create(Body, Secret, signer: null, Now);
        string second = WebhookSignature.Create(Body, Secret, signer: null, Now);

        // The symmetric half is deterministic, which is what lets a receiver deduplicate retries.
        Assert.Equal(first, second);
    }

    [Fact]
    public void AsymmetricSignature_CoversExactlyTheBytesThatWereSent()
    {
        (ISigner signer, VerificationKey key) = KeyPair();

        string header = WebhookSignature.Create(Body, Secret, signer, Now);

        // Verified the way an integrator would: rebuild "<unix seconds>.<body>" from the header and
        // the response body, and check it against the key published in the JWKS.
        Assert.True(VerifyAsymmetric(header, Body, key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("v1=abc")]
    [InlineData("t=1747000000")]
    [InlineData("t=not-a-number, v1=abc")]
    [InlineData("nonsense")]
    [InlineData("t==, v1=")]
    public void TryParse_MalformedHeader_IsRefused(string? header)
    {
        Assert.False(WebhookSignature.TryParse(header, out WebhookSignatureHeader parsed));
        Assert.Equal(0, parsed.UnixSeconds);
        Assert.Null(parsed.V1);
    }

    [Fact]
    public void TryParse_OverlongHeader_IsRefused() =>
        Assert.False(WebhookSignature.TryParse(new string('a', 5000), out _));

    [Fact]
    public void VerifySymmetric_MalformedHeader_IsRefusedWithoutThrowing() =>
        Assert.False(WebhookSignature.VerifySymmetric("nonsense", Body, Secret, Now, TimeSpan.FromMinutes(5)));

    [Fact]
    public void AlgorithmHeader_NamesBothSignatures()
    {
        Assert.Equal("DLE-Signature", WebhookSignature.SignatureHeader);
        Assert.Equal("DLE-Alg", WebhookSignature.AlgorithmHeader);
        Assert.Equal("HS256+Ed25519", WebhookSignature.AlgorithmValue);
    }

    /// <summary>Verifies the <c>v2</c> signature the way a receiver would.</summary>
    private static bool VerifyAsymmetric(string header, byte[] body, VerificationKey key)
    {
        if (!WebhookSignature.TryParse(header, out WebhookSignatureHeader parsed) || parsed.V2 is null)
        {
            return false;
        }

        byte[] timestamp = Encoding.UTF8.GetBytes(
            parsed.UnixSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

        byte[] signingInput = [.. timestamp, (byte)'.', .. body];

        return key.Verify(signingInput, parsed.V2);
    }

    private static (ISigner Signer, VerificationKey Key) KeyPair()
    {
        SigningKeyMaterial material = SigningKeyFactory.Generate(
            SignatureAlgorithms.Ed25519,
            SigningKeyPurposes.Webhook,
            notBefore: Now.AddDays(-1),
            notAfter: Now.AddDays(30));

        return (SigningKeyFactory.CreateSigner(material), SigningKeyFactory.CreateVerificationKey(material));
    }
}
