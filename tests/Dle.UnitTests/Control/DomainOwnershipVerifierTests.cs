using System.Buffers.Binary;
using System.Text;

using Dle.Control.Features.Domains;
using Dle.Crypto;
using Dle.Domain.Entities;

using Microsoft.Extensions.Options;

using Xunit;

namespace Dle.UnitTests.Control;

public sealed class DomainOwnershipVerifierTests
{
    private static readonly IOptions<CryptoOptions> Crypto = Options.Create(new CryptoOptions
    {
        MasterSecret = "test-master-secret-at-least-32-characters-long!",
    });

    [Fact]
    public async Task VerifyAsync_AcceptsMatchingPerTenantTxtChallengeAndRejectsForeignTenant()
    {
        var stubDns = new StubDnsTxtResolver();
        var verifier = new DomainOwnershipVerifier(Crypto, stubDns);

        var tenantA = Guid.Parse("01930000-0000-7000-8000-000000000001");
        var tenantB = Guid.Parse("01930000-0000-7000-8000-000000000002");

        string expectedA = verifier.GetExpectedTxtValue(tenantA, "go.brand.example");
        string expectedB = verifier.GetExpectedTxtValue(tenantB, "go.brand.example");

        Assert.NotEqual(expectedA, expectedB);
        Assert.Equal("_dle-verify.go.brand.example", DomainOwnershipVerifier.GetTxtHost("go.brand.example"));

        stubDns.Records["_dle-verify.go.brand.example"] = [expectedA];

        Assert.True(await verifier.VerifyAsync(
            new LinkDomain { TenantId = tenantA, Host = "go.brand.example" },
            CancellationToken.None));

        Assert.False(await verifier.VerifyAsync(
            new LinkDomain { TenantId = tenantB, Host = "go.brand.example" },
            CancellationToken.None));
    }

    [Fact]
    public void TryParseTxtAnswers_DecodesMultiChunkTxtRecordFromWirePacket()
    {
        byte[] query = UdpDnsTxtResolver.BuildQuery("_dle-verify.go.example");
        ReadOnlySpan<byte> txId = query.AsSpan(0, 2);

        byte[] part1 = Encoding.UTF8.GetBytes("dle-verify=");
        byte[] part2 = Encoding.UTF8.GetBytes("abc123XYZ");
        byte[] rdata = new byte[1 + part1.Length + 1 + part2.Length];
        rdata[0] = (byte)part1.Length;
        part1.CopyTo(rdata, 1);
        rdata[1 + part1.Length] = (byte)part2.Length;
        part2.CopyTo(rdata, 2 + part1.Length);

        using var ms = new MemoryStream();
        ms.Write(txId);
        // Response, NOERROR (0x8180), QD=1, AN=1, NS=0, AR=0
        ms.Write([0x81, 0x80, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00]);
        // Echo question section
        ms.Write(query.AsSpan(12));
        // Answer: pointer to offset 12 (0xC00C), TYPE=TXT (16), CLASS=IN (1), TTL=60, RDLENGTH
        ms.Write([0xC0, 0x0C, 0x00, 0x10, 0x00, 0x01, 0x00, 0x00, 0x00, 0x3C]);
        byte[] lenBytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(lenBytes, (ushort)rdata.Length);
        ms.Write(lenBytes);
        ms.Write(rdata);

        bool ok = UdpDnsTxtResolver.TryParseTxtAnswers(ms.ToArray(), txId, out IReadOnlyList<string> parsed);

        Assert.True(ok);
        Assert.Equal(["dle-verify=abc123XYZ"], parsed);
    }

    private sealed class StubDnsTxtResolver : IDnsTxtResolver
    {
        public Dictionary<string, IReadOnlyList<string>> Records { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<string>> ResolveTxtAsync(string hostName, CancellationToken cancellationToken) =>
            Task.FromResult(Records.TryGetValue(hostName, out IReadOnlyList<string>? values) ? values : []);
    }
}
