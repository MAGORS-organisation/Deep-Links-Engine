using System.Diagnostics.Metrics;
using System.Globalization;

using Dle.Domain.Analytics;
using Dle.Domain.Ports;
using Dle.Persistence.Fast.Configuration;
using Dle.Persistence.Fast.Telemetry;

namespace Dle.IntegrationTests.Persistence;

/// <summary>
/// The click stream write path: a bounded channel that drops rather than blocks, and a binary
/// <c>COPY</c> that puts the batch in the partitioned table (§C.3.2, FR-165, NFR-06).
/// </summary>
/// <remarks>
/// <para>
/// The trade this pipeline exists to make is stated in NFR-06: when the analytics layer falls behind,
/// telemetry is thrown away and the redirect is still served. A test that only asserted the happy
/// path would leave the important half — that a full channel neither blocks the caller nor loses the
/// count of what it lost — completely unexercised.
/// </para>
/// <para>
/// The <c>COPY</c> half needs a real server. A binary import carries type OIDs rather than names, so
/// one column in the wrong place or one value written as text into <c>character(2)</c> aborts the
/// whole batch — a class of defect no fake can reproduce and no unit test can see.
/// </para>
/// </remarks>
public sealed class ClickEventPipelineTests(DleInfrastructureFixture infrastructure)
    : DleIntegrationTest(infrastructure)
{
    [RequiresDockerFact]
    [Trait("Spec", "C.3.2")]
    public async Task CopyWriter_WritesABatch_AndEveryColumnArrivesWithItsDeclaredType()
    {
        Guid tenantId = await TestSeed.TenantAsync(Database, "copy", "full", Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, TestSeed.UniqueHost("copy"), cancellationToken: Ct);
        long linkId = await TestSeed.LinkAsync(Database, tenantId, domainId, "copied", "https://example.test/", cancellationToken: Ct);

        DateTimeOffset occurredAt = await ServerNowAsync();

        List<ClickEvent> batch =
        [
            new ClickEvent
            {
                Id = Guid.CreateVersion7(occurredAt),
                OccurredAt = occurredAt,
                TenantId = tenantId,
                LinkId = linkId,
                ClickId = "copy-1",
                IpHash = [1, 2, 3, 4],
                IpPrefix = "203.0.113.0/24",
                UaFamily = "Mobile Safari",
                OsFamily = "iOS",
                OsVersion = "18.1",
                DeviceClass = "phone",
                Country = "SK",
                Region = "BL",
                Language = "sk",
                ReferrerHost = "facebook.com",
                Channel = "in_app_fb",
                Decision = DecisionNames.Interstitial,
                AbBucket = 42,
                ConsentMode = "full",
                IsBot = false,
                SpoofedBot = true,
                LatencyMs = 7,
            },
            new ClickEvent
            {
                Id = Guid.CreateVersion7(occurredAt),
                OccurredAt = occurredAt,
                TenantId = tenantId,
                LinkId = linkId,
                ClickId = "copy-2",
                Decision = DecisionNames.NotFound,
                ConsentMode = "off",
                IsBot = false,
            },
        ];

        await using EdgeStores stores = EdgeStores.Open(Database);

        await stores.ClickWriter.WriteBatchAsync(batch, Ct);

        Assert.Equal(
            2L,
            await Sql.ScalarAsync<long>(
                Database.DataSource,
                "SELECT count(*) FROM click_events WHERE tenant_id = $1",
                [tenantId],
                Ct));

        // Read the typed columns back as text, so the assertion is about what PostgreSQL stored
        // rather than about what the driver would hand back.
        IReadOnlyList<string> row = await Sql.StringsAsync(
            Database.DataSource,
            """
            SELECT host(ip_prefix) || '/' || masklen(ip_prefix)
                || '|' || country
                || '|' || device_class
                || '|' || channel
                || '|' || decision
                || '|' || ab_bucket::text
                || '|' || latency_ms::text
                || '|' || encode(ip_hash, 'hex')
                || '|' || (extra ->> 'spoofed_bot')
            FROM click_events
            WHERE click_id = 'copy-1'
            """,
            cancellationToken: Ct);

        Assert.Single(row);
        Assert.Equal("203.0.113.0/24|SK|phone|in_app_fb|interstitial|42|7|01020304|true", row[0]);

        // SpoofedBot has no column of its own in §B.5.3 and is carried in extra. The row that was
        // not spoofed carries an empty object rather than a null, because the column is NOT NULL.
        Assert.Equal(
            "{}",
            await Sql.ScalarAsync<string>(
                Database.DataSource,
                "SELECT extra::text FROM click_events WHERE click_id = 'copy-2'",
                cancellationToken: Ct));
    }

    [RequiresDockerFact]
    [Trait("Spec", "NFR-06")]
    public void Sink_WhenTheChannelIsFull_DropsTheEventAndCountsIt()
    {
        // No database and no clock: the whole point of the bounded channel is that the decision to
        // drop is taken synchronously, on the request thread, without waiting for anything.
        FastPersistenceOptions options = new() { ClickEventChannelCapacity = 8 };
        FastPersistenceMetrics metrics = new(meterFactory: null);

        using (metrics)
        {
            ChannelClickEventSink sink = new(options, metrics);

            int accepted = 0;
            int refused = 0;

            for (int i = 0; i < 64; i++)
            {
                if (sink.TryWrite(Event(i)))
                {
                    accepted++;
                }
                else
                {
                    refused++;
                }
            }

            Assert.Equal(options.ClickEventChannelCapacity, accepted);
            Assert.Equal(64 - options.ClickEventChannelCapacity, refused);
            Assert.Equal(refused, sink.DroppedCount);
        }
    }

    [RequiresDockerFact]
    [Trait("Spec", "NFR-06")]
    public void Sink_WhenItDrops_PublishesTheAlertableCounter()
    {
        // §C.6 puts an alert on dle_click_events_dropped_total. Dropping is a deliberate trade under
        // load; a silent one would mean campaign numbers going quietly wrong with nothing to notice.
        long observed = 0;

        using MeterListener listener = new();

        listener.InstrumentPublished = (instrument, l) =>
        {
            if (string.Equals(instrument.Meter.Name, FastPersistenceMetrics.MeterName, StringComparison.Ordinal)
                && string.Equals(instrument.Name, "dle.click_events.dropped", StringComparison.Ordinal))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };

        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => Interlocked.Add(ref observed, measurement));
        listener.Start();

        FastPersistenceMetrics metrics = new(meterFactory: null);

        using (metrics)
        {
            ChannelClickEventSink sink = new(new FastPersistenceOptions { ClickEventChannelCapacity = 2 }, metrics);

            for (int i = 0; i < 10; i++)
            {
                _ = sink.TryWrite(Event(i));
            }

            Assert.Equal(8, sink.DroppedCount);
        }

        Assert.Equal(8, Interlocked.Read(ref observed));
    }

    [RequiresDockerFact]
    [Trait("Spec", "C.3.2")]
    public async Task Sink_AndCopyWriter_Together_PersistExactlyWhatTheChannelAccepted()
    {
        Guid tenantId = await TestSeed.TenantAsync(Database, "flood", "full", Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, TestSeed.UniqueHost("flood"), cancellationToken: Ct);
        long linkId = await TestSeed.LinkAsync(Database, tenantId, domainId, "flooded", "https://example.test/", cancellationToken: Ct);

        DateTimeOffset occurredAt = await ServerNowAsync();

        const int Capacity = 16;
        const int Offered = 200;

        FastPersistenceMetrics metrics = new(meterFactory: null);

        using (metrics)
        {
            ChannelClickEventSink sink = new(new FastPersistenceOptions { ClickEventChannelCapacity = Capacity }, metrics);

            for (int i = 0; i < Offered; i++)
            {
                _ = sink.TryWrite(Event(i, tenantId, linkId, occurredAt));
            }

            Assert.Equal(Offered - Capacity, sink.DroppedCount);

            List<ClickEvent> drained = [];

            while (sink.Reader.TryRead(out ClickEvent? item))
            {
                drained.Add(item);
            }

            Assert.Equal(Capacity, drained.Count);

            await using EdgeStores stores = EdgeStores.Open(Database);
            await stores.ClickWriter.WriteBatchAsync(drained, Ct);
        }

        Assert.Equal(
            (long)Capacity,
            await Sql.ScalarAsync<long>(
                Database.DataSource,
                "SELECT count(*) FROM click_events WHERE tenant_id = $1",
                [tenantId],
                Ct));
    }

    /// <summary>The server's current instant, so that rows land in a partition that exists.</summary>
    private async Task<DateTimeOffset> ServerNowAsync()
    {
        DateTime now = await Sql.ScalarAsync<DateTime>(
            Database.DataSource,
            "SELECT (now() AT TIME ZONE 'UTC')",
            cancellationToken: Ct);

        return new DateTimeOffset(now, TimeSpan.Zero);
    }

    /// <summary>An event with no database dependency, for the pure channel tests.</summary>
    private static ClickEvent Event(int index) =>
        Event(index, Guid.Empty, 0L, DateTimeOffset.UnixEpoch);

    /// <summary>An event that can be written to the seeded tenant and link.</summary>
    private static ClickEvent Event(int index, Guid tenantId, long linkId, DateTimeOffset occurredAt) => new()
    {
        Id = Guid.CreateVersion7(occurredAt),
        OccurredAt = occurredAt,
        TenantId = tenantId,
        LinkId = linkId,
        ClickId = string.Create(CultureInfo.InvariantCulture, $"flood-{index}"),
        Decision = DecisionNames.Web,
        ConsentMode = "aggregate_only",
        IsBot = false,
    };
}
