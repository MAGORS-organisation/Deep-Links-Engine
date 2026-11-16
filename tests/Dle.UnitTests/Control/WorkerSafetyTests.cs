using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

using Dle.Control.Workers;
using Dle.Domain.Abuse;
using Xunit;

namespace Dle.UnitTests.Control;

/// <summary>
/// Two workers act on live data without a human: one withdraws links, one replaces the GeoIP
/// database the edge reads. These tests pin the conditions under which they are allowed to.
/// </summary>
public sealed class WorkerSafetyTests
{
    private static readonly byte[] MetadataMarker =
        [0xAB, 0xCD, 0xEF, .. Encoding.ASCII.GetBytes("MaxMind.com")];

    [Theory]
    [InlineData(UrlSafetyLevel.Malicious, "urlhaus", true)]
    [InlineData(UrlSafetyLevel.Blocked, "blocklist", true)]
    [InlineData(UrlSafetyLevel.Suspicious, "urlhaus", false)]
    [InlineData(UrlSafetyLevel.Blocked, TargetUrlPolicy.AddressSource, false)]
    public void WarrantsQuarantine_OnlyAReputationVerdictWithdrawsALiveLink(
        UrlSafetyLevel level,
        string source,
        bool expected)
    {
        // A host that fails to resolve during the nightly pass is reported as Blocked by the address
        // check. That must be flagged, never turned into a quarantine of a link that is being used.
        UrlSafetyVerdict verdict = UrlSafetyVerdict.Reject(level, source, "reason");

        Assert.Equal(expected, UrlReputationWorker.WarrantsQuarantine(verdict));
    }

    [Theory]
    [InlineData(0, 2000)]
    [InlineData(1500, 2000)]
    [InlineData(2000, 2000)]
    public void WindowOffset_EverythingFitsInOnePass_StartsAtTheBeginning(int total, int limit)
    {
        Assert.Equal(0, UrlReputationWorker.WindowOffset(total, limit, pass: 12_345));
    }

    [Theory]
    [InlineData(5000, 2000)]
    [InlineData(7001, 2000)]
    [InlineData(9, 4)]
    public void WindowOffset_SuccessivePasses_ReachEveryLink(int total, int limit)
    {
        bool[] seen = new bool[total];

        // Starting anywhere: the pass ordinal comes from the clock, not from zero.
        for (long pass = 777; pass < 777 + total; pass++)
        {
            int offset = UrlReputationWorker.WindowOffset(total, limit, pass);

            Assert.InRange(offset, 0, total - 1);

            for (int i = offset; i < Math.Min(offset + limit, total); i++)
            {
                seen[i] = true;
            }
        }

        Assert.DoesNotContain(false, seen);
    }

    [Fact]
    public async Task UnpackAsync_PlainDatabase_IsAcceptedAsIs()
    {
        string path = TempFile([.. new byte[4096], .. MetadataMarker, .. new byte[32]]);

        try
        {
            string database = await GeoIpUpdateWorker.UnpackAsync(path, maxBytes: 1 << 20, TestContext.Current.CancellationToken);

            Assert.Equal(path, database);
            Assert.True(File.Exists(database));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task UnpackAsync_MaxMindArchive_YieldsTheDatabaseInsideIt()
    {
        byte[] databaseBytes = [.. new byte[2048], .. MetadataMarker, .. new byte[16]];
        string path = TempFile(TarGz(("GeoLite2-Country_20261110/COPYRIGHT.txt", "text"u8.ToArray()),
            ("GeoLite2-Country_20261110/GeoLite2-Country.mmdb", databaseBytes)));

        string? database = null;

        try
        {
            database = await GeoIpUpdateWorker.UnpackAsync(path, maxBytes: 1 << 20, TestContext.Current.CancellationToken);

            Assert.NotEqual(path, database);
            Assert.Equal(databaseBytes, await File.ReadAllBytesAsync(database, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(path), "the archive is removed once it has been unpacked");
        }
        finally
        {
            File.Delete(path);

            if (database is not null)
            {
                File.Delete(database);
            }
        }
    }

    [Fact]
    public async Task UnpackAsync_HtmlErrorPage_IsRefusedAndRemoved()
    {
        string path = TempFile(Encoding.UTF8.GetBytes("<html><body>Rate limited</body></html>"));

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => GeoIpUpdateWorker.UnpackAsync(path, maxBytes: 1 << 20, TestContext.Current.CancellationToken));

            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task UnpackAsync_ArchiveWithoutADatabase_IsRefused()
    {
        string path = TempFile(TarGz(("readme/README.txt", "nothing here"u8.ToArray())));

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => GeoIpUpdateWorker.UnpackAsync(path, maxBytes: 1 << 20, TestContext.Current.CancellationToken));

            Assert.False(File.Exists(path));
            Assert.False(File.Exists(path + ".mmdb"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task UnpackAsync_DatabaseLargerThanTheBound_IsRefused()
    {
        string path = TempFile(TarGz(("db/GeoLite2-Country.mmdb", [.. new byte[8192], .. MetadataMarker])));

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => GeoIpUpdateWorker.UnpackAsync(path, maxBytes: 1024, TestContext.Current.CancellationToken));

            Assert.False(File.Exists(path + ".mmdb"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TempFile(byte[] content)
    {
        string path = Path.Combine(Path.GetTempPath(), "dle-geoip-" + Guid.NewGuid().ToString("N") + ".download");
        File.WriteAllBytes(path, content);

        return path;
    }

    private static byte[] TarGz(params (string Name, byte[] Content)[] entries)
    {
        using MemoryStream buffer = new();

        using (GZipStream gzip = new(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (TarWriter tar = new(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach ((string name, byte[] content) in entries)
            {
                PaxTarEntry entry = new(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(content),
                };

                tar.WriteEntry(entry);
            }
        }

        return buffer.ToArray();
    }
}
