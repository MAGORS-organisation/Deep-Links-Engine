using System.Globalization;
using System.Text.RegularExpressions;

using Dle.Domain.Ports;

namespace Dle.IntegrationTests.Persistence;

/// <summary>
/// The pruning §B.6.3 calls the critical detail: a click lookup bounded by the timestamp its own
/// identifier carries touches one or two partitions, not the whole retention.
/// </summary>
/// <remarks>
/// <para>
/// <c>click_events</c> is range partitioned by day with a retention of a hundred and eighty days. A
/// lookup by <c>click_id</c> alone has no pruning predicate, so the planner has to visit every
/// partition's index — a hundred and eighty of them at full retention. The cost therefore grows with
/// how long the system has been running, which makes it invisible in every test written before the
/// first partitions age out and visible in production about six months after launch. That is the
/// silent debt §B.6.3 warns about, and the bounded window is the whole remedy.
/// </para>
/// <para>
/// The bound is not guessed by the caller: the click identifier carries its own encrypted timestamp,
/// which <c>IClickIdCodec.TryDecode</c> turns back into an instant before the lookup is made.
/// </para>
/// </remarks>
public sealed partial class PartitionPruningTests(DleInfrastructureFixture infrastructure)
    : DleIntegrationTest(infrastructure)
{
    /// <summary>Matches a daily partition of the click stream in an <c>EXPLAIN</c> plan.</summary>
    [GeneratedRegex(@"click_events_p\d{8}", RegexOptions.CultureInvariant)]
    private static partial Regex PartitionName { get; }

    [RequiresDockerFact]
    [Trait("Spec", "B.6.3")]
    public async Task ClickLookup_BoundedByTheClickIdsOwnTimestamp_TouchesAtMostTwoPartitions()
    {
        Fixture seeded = await SeedAcrossDaysAsync();

        // Literals rather than parameters: partition pruning at plan time needs the bounds to be
        // known when the plan is made, which is exactly the shape the Dapper store produces once the
        // extended protocol has bound its parameters.
        DateTimeOffset target = seeded.Days[2];

        string plan = await Sql.ExplainAsync(
            Database.DataSource,
            string.Create(
                CultureInfo.InvariantCulture,
                $"""
                 SELECT ce.id
                 FROM click_events ce
                 WHERE ce.click_id = 'click-2'
                   AND ce.occurred_at BETWEEN TIMESTAMPTZ '{Iso(target.AddMinutes(-30))}'
                                          AND TIMESTAMPTZ '{Iso(target.AddMinutes(30))}'
                 ORDER BY ce.occurred_at DESC
                 LIMIT 1
                 """),
            "ANALYZE, BUFFERS",
            cancellationToken: Ct);

        HashSet<string> touched = PartitionsIn(plan);

        Assert.True(
            touched.Count is > 0 and <= 2,
            string.Create(
                CultureInfo.InvariantCulture,
                $"A bounded click lookup touched {touched.Count} partitions ({string.Join(", ", touched)}), "
                + $"and §B.6.3 requires one or two. The plan was:\n{plan}"));
    }

    [RequiresDockerFact]
    [Trait("Spec", "B.6.3")]
    public async Task ClickLookup_WithoutATimeBound_TouchesEveryPartition()
    {
        // The counter-example, and the reason the bound exists at all. Without it the same lookup
        // reads every partition — which is invisible while there are five of them and fatal when
        // there are a hundred and eighty.
        Fixture seeded = await SeedAcrossDaysAsync();

        string plan = await Sql.ExplainAsync(
            Database.DataSource,
            "SELECT ce.id FROM click_events ce WHERE ce.click_id = 'click-2' LIMIT 1",
            "ANALYZE, BUFFERS",
            cancellationToken: Ct);

        HashSet<string> touched = PartitionsIn(plan);

        Assert.True(
            touched.Count > 2,
            "An unbounded click lookup was expected to touch every partition, which is what makes "
            + "the bounded one worth asserting. It touched " + touched.Count + ".");

        Assert.True(seeded.Days.Count >= 5);
    }

    [RequiresDockerFact]
    [Trait("Spec", "B.6.3")]
    public async Task ClickLookup_ThroughTheStore_FindsTheClickInTheRightPartition()
    {
        Fixture seeded = await SeedAcrossDaysAsync();

        await using EdgeStores stores = EdgeStores.Open(Database);

        DateTimeOffset target = seeded.Days[2];

        ClickRecord? found = await stores.Clicks.FindByClickIdAsync(
            "click-2",
            target.AddMinutes(-30),
            target.AddMinutes(30),
            Ct);

        Assert.NotNull(found);
        Assert.Equal(seeded.TenantId, found.TenantId);
        Assert.Equal(seeded.LinkId, found.LinkId);

        // ip_prefix is projected as host()/masklen() so that it round trips to the exact spelling
        // IIpHasher.Prefix produces and the candidate filter compares against.
        Assert.Equal("203.0.113.0/24", found.IpPrefix);

        // A window that does not contain the click finds nothing, rather than quietly widening.
        Assert.Null(
            await stores.Clicks.FindByClickIdAsync(
                "click-2",
                target.AddDays(1),
                target.AddDays(1).AddMinutes(30),
                Ct));
    }

    [RequiresDockerFact]
    [Trait("Spec", "B.6.3")]
    public async Task CandidateLookup_ExcludesBotClicksAndStaysInsideTheWindow()
    {
        Fixture seeded = await SeedAcrossDaysAsync();

        await using EdgeStores stores = EdgeStores.Open(Database);

        DateTimeOffset target = seeded.Days[2];

        IReadOnlyList<ClickRecord> candidates = await stores.Clicks.FindCandidatesAsync(
            seeded.TenantId,
            target.AddMinutes(-30),
            target.AddMinutes(30),
            "203.0.113.0/24",
            "iOS",
            Ct);

        Assert.Single(candidates);
        Assert.Equal("click-2", candidates[0].ClickId);

        // A crawler's click is a real row and stays in the stream for reporting, but attributing a
        // human install to it would be worse than not attributing at all.
        IReadOnlyList<ClickRecord> withBot = await stores.Clicks.FindCandidatesAsync(
            seeded.TenantId,
            seeded.Days[0].AddMinutes(-30),
            seeded.Days[0].AddMinutes(30),
            null,
            null,
            Ct);

        Assert.DoesNotContain(withBot, record => string.Equals(record.ClickId, "click-bot", StringComparison.Ordinal));
    }

    /// <summary>
    /// Seeds one click per day across five consecutive days that the migration pre-created
    /// partitions for, plus one crawler click.
    /// </summary>
    /// <remarks>
    /// The days are derived from the server's own clock rather than from the test process's, because
    /// the partitions were created by the migration against that same clock. Reading it is not the
    /// ambient wall-clock dependency SHARED-KERNEL §17.2 forbids: it is reading a value out of the
    /// system under test.
    /// </remarks>
    private async Task<Fixture> SeedAcrossDaysAsync()
    {
        DateTime today = await Sql.ScalarAsync<DateTime>(
            Database.DataSource,
            "SELECT (now() AT TIME ZONE 'UTC')::date",
            cancellationToken: Ct);

        DateTimeOffset midnight = new(today, TimeSpan.Zero);

        // dle_click_events_maintain pre-creates yesterday through seven days ahead, so days 0..4
        // starting at midnight today are all real partitions.
        List<DateTimeOffset> days =
        [
            midnight.AddHours(12),
            midnight.AddDays(1).AddHours(12),
            midnight.AddDays(2).AddHours(12),
            midnight.AddDays(3).AddHours(12),
            midnight.AddDays(4).AddHours(12),
        ];

        Guid tenantId = await TestSeed.TenantAsync(Database, "pruning", "full", Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, TestSeed.UniqueHost("pruning"), cancellationToken: Ct);
        long linkId = await TestSeed.LinkAsync(Database, tenantId, domainId, "pruned", "https://example.test/", cancellationToken: Ct);

        for (int i = 0; i < days.Count; i++)
        {
            await TestSeed.ClickEventAsync(
                Database,
                tenantId,
                linkId,
                string.Create(CultureInfo.InvariantCulture, $"click-{i}"),
                days[i],
                ipPrefix: "203.0.113.0/24",
                osFamily: "iOS",
                cancellationToken: Ct);
        }

        await TestSeed.ClickEventAsync(
            Database,
            tenantId,
            linkId,
            "click-bot",
            days[0],
            decision: "preview",
            isBot: true,
            ipPrefix: "203.0.113.0/24",
            osFamily: "iOS",
            cancellationToken: Ct);

        _ = await Sql.ExecuteAsync(Database.DataSource, "ANALYZE click_events", cancellationToken: Ct);

        return new Fixture(tenantId, linkId, days);
    }

    /// <summary>The distinct daily partitions named in a plan.</summary>
    /// <param name="plan">The EXPLAIN output.</param>
    /// <returns>The partition names.</returns>
    private static HashSet<string> PartitionsIn(string plan)
    {
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (Match match in PartitionName.Matches(plan))
        {
            _ = names.Add(match.Value);
        }

        return names;
    }

    /// <summary>An instant in the form PostgreSQL parses as a <c>timestamptz</c> literal.</summary>
    private static string Iso(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture) + "+00";

    /// <summary>What the seed produced.</summary>
    private sealed record Fixture(Guid TenantId, long LinkId, IReadOnlyList<DateTimeOffset> Days);
}
