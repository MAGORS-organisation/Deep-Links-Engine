using System.Globalization;
using Dle.Domain.Routing;
using Xunit;

namespace Dle.UnitTests.Routing;

/// <summary>
/// FR-125: the A/B bucket is derived from the click id, never from a random source. If the mapping
/// were not stable, one visitor could see variant A on the interstitial and variant B in the store
/// referrer, and the experiment would be unreadable.
/// </summary>
public sealed class ConsistentBucketTests
{
    [Fact]
    public void Of_SameClickId_AlwaysYieldsTheSameBucket()
    {
        const string ClickId = "01JQ8Z0K7M8T9RVEXAMPLE";

        short first = ConsistentBucket.Of(ClickId);
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(first, ConsistentBucket.Of(ClickId));
        }
    }

    [Fact]
    public void Of_EmptyClickId_UsesTheHashSeedRatherThanFailing()
    {
        Assert.Equal(37, ConsistentBucket.Of(string.Empty));
    }

    [Fact]
    public void Of_NullClickId_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ConsistentBucket.Of(null!));
    }

    [Fact]
    public void Of_AnyClickId_StaysInsideTheBucketSpace()
    {
        for (int i = 0; i < 5000; i++)
        {
            short bucket = ConsistentBucket.Of(string.Create(CultureInfo.InvariantCulture, $"click-{i}"));
            Assert.InRange(bucket, 0, ConsistentBucket.BucketCount - 1);
        }
    }

    [Fact]
    public void Of_LongClickIdBeyondTheStackBuffer_IsStillDeterministic()
    {
        string clickId = new('x', 4096);

        Assert.Equal(ConsistentBucket.Of(clickId), ConsistentBucket.Of(clickId));
        Assert.InRange(ConsistentBucket.Of(clickId), 0, 99);
    }

    [Fact]
    public void Of_NonAsciiClickId_IsStillDeterministic()
    {
        const string ClickId = "klik-áčž-F600";

        Assert.Equal(ConsistentBucket.Of(ClickId), ConsistentBucket.Of(ClickId));
    }

    [Fact]
    public void Of_ManyClickIds_CoversEveryBucketReasonablyEvenly()
    {
        const int Samples = 100_000;
        int[] counts = new int[ConsistentBucket.BucketCount];

        for (int i = 0; i < Samples; i++)
        {
            counts[ConsistentBucket.Of(string.Create(CultureInfo.InvariantCulture, $"01JQ8Z0K7M8T9RV{i:D8}"))]++;
        }

        const int Expected = Samples / ConsistentBucket.BucketCount;
        for (int bucket = 0; bucket < counts.Length; bucket++)
        {
            // A 40 % band around the mean. Wide enough that a healthy hash never trips it, narrow
            // enough that a hash collapsing onto a few buckets does.
            Assert.InRange(counts[bucket], (int)(Expected * 0.6), (int)(Expected * 1.4));
        }
    }

    [Fact]
    public void Of_CloselyRelatedClickIds_DoNotLandInTheSameBucket()
    {
        var buckets = new HashSet<short>();
        for (int i = 0; i < 200; i++)
        {
            buckets.Add(ConsistentBucket.Of(string.Create(CultureInfo.InvariantCulture, $"clk{i:D4}")));
        }

        // Sequential ids are the realistic input; a hash that ignores the tail would produce one or
        // two buckets here.
        Assert.True(buckets.Count > 50, $"Only {buckets.Count} distinct buckets for 200 sequential click ids.");
    }

    [Fact]
    public void BucketCount_IsOneHundred()
    {
        Assert.Equal(100, ConsistentBucket.BucketCount);
    }
}
