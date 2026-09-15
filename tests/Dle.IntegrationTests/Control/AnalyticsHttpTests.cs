using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Dle.Analytics.Postgres;
using Dle.Control.Configuration;
using Dle.Control.Identity;
using Dle.Domain.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Dle.IntegrationTests.Control;

/// <summary>
/// The analytics of the control plane through HTTP against a real PostgreSQL (§B.7.6, FR-300 to
/// FR-312, ADR-006, ADR-008): the click and install series, the breakdowns, the funnel and the
/// attribution quality panel computed from raw events, the same figures served from the rollups
/// after the rollup job has run, the CSV and Parquet exports, the live stream, the retention job
/// that drops expired partitions and forgets IP prefixes, and the whole-tenant export.
/// </summary>
/// <remarks>
/// Every test seeds its own tenant, so the figures asserted are exact rather than "at least".
/// The retention test is the one exception: partitions are a property of the table, not of a
/// tenant, so it asserts on its own rows and on the run ledger rather than on partition names.
/// </remarks>
[SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "The host is handed to DleIntegrationTest.DisposeWithTest, which disposes it at "
                    + "the end of the test.")]
public sealed class AnalyticsHttpTests(DleInfrastructureFixture infrastructure)
    : DleIntegrationTest(infrastructure)
{
    private const string Analytics = "/api/v1/analytics";

    [RequiresDockerFact]
    [Trait("Spec", "FR-300")]
    public async Task TimeSeries_CountsClicksAndInstalls_LeavesBotsOutUnlessAsked_AndTotalsTheFunnel()
    {
        Fixture fixture = await SeedAsync("analytics-series");
        Seeded seeded = await SeedEventsAsync(fixture);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage hourly = await fixture.Key.GetAsync(client, Analytics + "/clicks?grain=hour", Ct);
        Assert.Equal(HttpStatusCode.OK, hourly.StatusCode);
        using JsonDocument series = JsonDocument.Parse(await hourly.Content.ReadAsStringAsync(Ct));
        Assert.Equal("hour", series.RootElement.GetProperty("grain").GetString());
        Assert.Equal(2, SumOf(series.RootElement.GetProperty("points"), "clicks"));
        Assert.Equal(1, SumOf(series.RootElement.GetProperty("points"), "installs"));
        JsonElement totals = series.RootElement.GetProperty("totals");
        Assert.Equal(2, totals.GetProperty("clicks").GetInt64());
        Assert.Equal(1, totals.GetProperty("installs").GetInt64());
        Assert.Equal(1, totals.GetProperty("attributed").GetInt64());

        using HttpResponseMessage withBots = await fixture.Key.GetAsync(client, Analytics + "/clicks?grain=day&include_bots=true", Ct);
        using JsonDocument daily = JsonDocument.Parse(await withBots.Content.ReadAsStringAsync(Ct));
        Assert.Equal("day", daily.RootElement.GetProperty("grain").GetString());
        Assert.Equal(3, SumOf(daily.RootElement.GetProperty("points"), "clicks"));

        using HttpResponseMessage oneLink = await fixture.Key.GetAsync(
            client,
            Analytics + "/clicks?link_id=" + seeded.OtherLinkId.ToString(CultureInfo.InvariantCulture),
            Ct);
        using JsonDocument filtered = JsonDocument.Parse(await oneLink.Content.ReadAsStringAsync(Ct));
        Assert.Equal(0, SumOf(filtered.RootElement.GetProperty("points"), "clicks"));

        using HttpResponseMessage installs = await fixture.Key.GetAsync(client, Analytics + "/installs?platform=android", Ct);
        Assert.Equal(HttpStatusCode.OK, installs.StatusCode);
        using JsonDocument installSeries = JsonDocument.Parse(await installs.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, SumOf(installSeries.RootElement.GetProperty("points"), "installs"));
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-301")]
    public async Task Breakdown_GroupsByPlatform_AndRefusesADimensionOffTheAllowlist()
    {
        Fixture fixture = await SeedAsync("analytics-breakdown");
        _ = await SeedEventsAsync(fixture);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await fixture.Key.GetAsync(client, Analytics + "/breakdown?dimension=platform", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("platform", document.RootElement.GetProperty("dimension").GetString());
        Dictionary<string, long> clicksByKey = document.RootElement.GetProperty("rows")
            .EnumerateArray()
            .ToDictionary(row => row.GetProperty("key").GetString()!, row => row.GetProperty("clicks").GetInt64(), StringComparer.Ordinal);
        Assert.Equal(1, clicksByKey["ios"]);
        Assert.Equal(1, clicksByKey["android"]);
        Assert.False(clicksByKey.ContainsKey("bot"), "bots are left out unless asked for");

        // Grouping by a column the caller names is exactly the SQL injection surface a breakdown
        // has; anything off the allowlist is refused before a query is built.
        using HttpResponseMessage refused = await fixture.Key.GetAsync(client, Analytics + "/breakdown?dimension=tenant_id;drop", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync(Ct));
        Assert.Equal(ProblemCodes.ValidationFailed, problem.RootElement.GetProperty("type").GetString());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("dimension", out _));
    }

    [RequiresDockerFact]
    [Trait("Spec", "ADR-008")]
    public async Task Funnel_AndAttributionQuality_ComeFromTheSameEvents()
    {
        Fixture fixture = await SeedAsync("analytics-funnel");
        _ = await SeedEventsAsync(fixture);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage funnel = await fixture.Key.GetAsync(client, Analytics + "/funnels", Ct);
        Assert.Equal(HttpStatusCode.OK, funnel.StatusCode);
        using JsonDocument summary = JsonDocument.Parse(await funnel.Content.ReadAsStringAsync(Ct));
        Assert.Equal(2, summary.RootElement.GetProperty("clicks").GetInt64());
        Assert.Equal(1, summary.RootElement.GetProperty("installs").GetInt64());
        Assert.Equal(1, summary.RootElement.GetProperty("attributed").GetInt64());
        Assert.Equal(0, summary.RootElement.GetProperty("conversions").GetInt64());
        Assert.True(summary.RootElement.TryGetProperty("conversion_rate", out _));

        using HttpResponseMessage quality = await fixture.Key.GetAsync(client, Analytics + "/attribution-quality", Ct);
        Assert.Equal(HttpStatusCode.OK, quality.StatusCode);
        using JsonDocument panel = JsonDocument.Parse(await quality.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, panel.RootElement.GetProperty("deterministic").GetInt64());
        Assert.Equal(0, panel.RootElement.GetProperty("probabilistic").GetInt64());
        Assert.Equal(0, panel.RootElement.GetProperty("unmatched").GetInt64());
        JsonElement byMatchType = Assert.Single(panel.RootElement.GetProperty("by_match_type").EnumerateArray());
        Assert.Equal("install_referrer", byMatchType.GetProperty("match_type").GetString());
        Assert.Equal(1, byMatchType.GetProperty("count").GetInt64());
    }

    [RequiresDockerTheory]
    [Trait("Spec", "FR-300")]
    [InlineData("from=2026-02-01T00:00:00Z&to=2026-01-01T00:00:00Z", "from")]
    [InlineData("from=2020-01-01T00:00:00Z&to=2026-01-01T00:00:00Z", "from")]
    [InlineData("from=yesterday", "from")]
    [InlineData("link_id=abc", "link_id")]
    public async Task Queries_WithAnUnusableWindowOrFilter_AreValidationFailedOnTheField(string query, string field)
    {
        Fixture fixture = await SeedAsync("analytics-validation-" + field + query.Length.ToString(CultureInfo.InvariantCulture));
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage response = await fixture.Key.GetAsync(client, Analytics + "/clicks?" + query, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(ProblemCodes.ValidationFailed, problem.RootElement.GetProperty("type").GetString());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _), "no error recorded for " + field);
    }

    [RequiresDockerFact]
    [Trait("TestCase", "TC-166")]
    public async Task Figures_AreTheCallersTenantsOnly_AViewerMayRead_AndNobodyElse()
    {
        Fixture fixture = await SeedAsync("analytics-scope");
        _ = await SeedEventsAsync(fixture);
        Guid otherTenant = await TestSeed.TenantAsync(Database, "analytics-scope-other", cancellationToken: Ct);
        Guid otherDomain = await TestSeed.DomainAsync(Database, otherTenant, TestSeed.UniqueHost("analytics-scope-other"), cancellationToken: Ct);
        long theirLink = await TestSeed.LinkAsync(Database, otherTenant, otherDomain, "theirs", "https://example.com/", cancellationToken: Ct);
        for (int i = 0; i < 5; i++)
        {
            await TestSeed.ClickEventAsync(
                Database, otherTenant, theirLink, fixture.Tag + "-their-" + i.ToString(CultureInfo.InvariantCulture), DateTimeOffset.UtcNow.AddHours(-1), cancellationToken: Ct);
        }

        ControlCredentials viewer = await ControlCredentials.IssueApiKeyAsync(fixture.Host, Database, fixture.TenantId, "viewer", Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage asViewer = await viewer.GetAsync(client, Analytics + "/funnels", Ct);
        Assert.Equal(HttpStatusCode.OK, asViewer.StatusCode);
        using JsonDocument summary = JsonDocument.Parse(await asViewer.Content.ReadAsStringAsync(Ct));
        Assert.Equal(2, summary.RootElement.GetProperty("clicks").GetInt64());

        using HttpResponseMessage anonymous = await client.GetAsync(new Uri(Analytics + "/funnels", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-310")]
    public async Task Export_WritesCsvOrParquet_NamedAfterTheReportAndWindow()
    {
        Fixture fixture = await SeedAsync("analytics-export");
        _ = await SeedEventsAsync(fixture);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage csv = await fixture.Key.GetAsync(client, Analytics + "/export?report=timeseries&format=csv&grain=day", Ct);
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("dle-timeseries-", csv.Content.Headers.ContentDisposition?.FileName?.Trim('"'), StringComparison.Ordinal);
        Assert.EndsWith(".csv", csv.Content.Headers.ContentDisposition?.FileName?.Trim('"'), StringComparison.Ordinal);
        string[] lines = (await csv.Content.ReadAsStringAsync(Ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 2, "a header and at least one bucket");
        Assert.Contains("clicks", lines[0], StringComparison.Ordinal);

        using HttpResponseMessage parquet = await fixture.Key.GetAsync(client, Analytics + "/export?report=breakdown&dimension=platform&format=parquet", Ct);
        Assert.Equal(HttpStatusCode.OK, parquet.StatusCode);
        Assert.Equal("application/vnd.apache.parquet", parquet.Content.Headers.ContentType?.MediaType);
        byte[] bytes = await parquet.Content.ReadAsByteArrayAsync(Ct);
        Assert.True(bytes.Length > 8, "a Parquet file has a header and a footer");
        Assert.Equal("PAR1", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("PAR1", System.Text.Encoding.ASCII.GetString(bytes, bytes.Length - 4, 4));

        using HttpResponseMessage quality = await fixture.Key.GetAsync(client, Analytics + "/export?report=attribution-quality&format=csv", Ct);
        Assert.Equal(HttpStatusCode.OK, quality.StatusCode);
        Assert.Contains("install_referrer", await quality.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        using HttpResponseMessage unknown = await fixture.Key.GetAsync(client, Analytics + "/export?report=everything&format=csv", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(await unknown.Content.ReadAsStringAsync(Ct));
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("report", out _));
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-311")]
    public async Task Stream_SendsTheFunnelAsAServerSentEvent()
    {
        Fixture fixture = await SeedAsync("analytics-stream");
        _ = await SeedEventsAsync(fixture);
        using HttpClient client = fixture.Host.CreateDirectClient();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(Analytics + "/stream?intervalSeconds=2", UriKind.Relative));
        request.Headers.Add(DleKeyAuthenticationOptions.ApiKeyHeader, fixture.Key.Token);
        using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token);
        using StreamReader reader = new(body);
        string? data = null;

        while (await reader.ReadLineAsync(timeout.Token) is string line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data = line["data:".Length..].Trim();
                break;
            }
        }

        Assert.NotNull(data);
        using JsonDocument first = JsonDocument.Parse(data);
        Assert.Equal(2, first.RootElement.GetProperty("clicks").GetInt64());
        Assert.Equal(1, first.RootElement.GetProperty("installs").GetInt64());
    }

    [RequiresDockerFact]
    [Trait("Spec", "ADR-006")]
    public async Task Rollups_AggregateTheRawEvents_AndAnAlignedQueryIsServedFromThem()
    {
        Fixture fixture = await SeedAsync("analytics-rollups");
        Seeded seeded = await SeedEventsAsync(fixture);
        using HttpClient client = fixture.Host.CreateDirectClient();
        IRollupService rollups = fixture.Host.Services.GetRequiredService<IRollupService>();

        RollupRunResult run = await rollups.RunAsync(Ct);

        Assert.True(run.DidWork, "the first run has the whole window to aggregate");
        Assert.True(run.ClickRowsHourly >= 1, "the seeded clicks produce hourly rollup rows");
        Assert.Equal(
            2L,
            await Sql.ScalarAsync<long>(
                Database.DataSource,
                "SELECT coalesce(sum(clicks), 0)::bigint FROM click_rollup_hourly WHERE tenant_id = $1 AND is_bot = false",
                [fixture.TenantId],
                Ct));
        Assert.Equal(
            1L,
            await Sql.ScalarAsync<long>(
                Database.DataSource,
                "SELECT count(*) FROM analytics_rollup_state WHERE name = 'click_rollup_hourly' AND covered_through >= $1",
                [seeded.AlignedTo],
                Ct));

        // A second run re-aggregates only the overlap window behind the high-water mark, so that
        // late events are counted; the upsert leaves the figures exactly where they were.
        RollupRunResult again = await rollups.RunAsync(Ct);
        Assert.True(again.HourlyThrough >= run.HourlyThrough, "the high-water mark never moves back");
        Assert.Equal(
            2L,
            await Sql.ScalarAsync<long>(
                Database.DataSource,
                "SELECT coalesce(sum(clicks), 0)::bigint FROM click_rollup_hourly WHERE tenant_id = $1 AND is_bot = false",
                [fixture.TenantId],
                Ct));

        // Hour-aligned edges inside the covered range: the store answers from the rollup and the
        // figures are the ones the raw events gave.
        string window = string.Create(
            CultureInfo.InvariantCulture,
            $"from={seeded.AlignedFrom:yyyy-MM-dd'T'HH:mm:ss'Z'}&to={seeded.AlignedTo:yyyy-MM-dd'T'HH:mm:ss'Z'}");
        using HttpResponseMessage aligned = await fixture.Key.GetAsync(client, Analytics + "/clicks?grain=hour&" + window, Ct);
        Assert.Equal(HttpStatusCode.OK, aligned.StatusCode);
        using JsonDocument series = JsonDocument.Parse(await aligned.Content.ReadAsStringAsync(Ct));
        Assert.Equal(2, SumOf(series.RootElement.GetProperty("points"), "clicks"));

        using HttpResponseMessage breakdown = await fixture.Key.GetAsync(client, Analytics + "/breakdown?dimension=platform&" + window, Ct);
        using JsonDocument rows = JsonDocument.Parse(await breakdown.Content.ReadAsStringAsync(Ct));
        Assert.Equal(2, SumOf(rows.RootElement.GetProperty("rows"), "clicks"));
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-312")]
    public async Task Retention_ForgetsIpPrefixes_DropsExpiredDays_AndKeepsALedger()
    {
        Fixture fixture = await SeedAsync(
            "analytics-retention",
            settings =>
            {
                settings["Dle:Privacy:Retention:RawDays"] = "30";
                settings["Dle:Privacy:Retention:IpPrefixDays"] = "1";
            });
        long linkId = await TestSeed.LinkAsync(Database, fixture.TenantId, fixture.DomainId, "kept", "https://example.com/", cancellationToken: Ct);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string recent = fixture.Tag + "-recent";
        string expired = fixture.Tag + "-expired";
        await TestSeed.ClickEventAsync(Database, fixture.TenantId, linkId, recent, now.AddDays(-2), ipPrefix: "203.0.113.0/24", cancellationToken: Ct);
        await TestSeed.ClickEventAsync(Database, fixture.TenantId, linkId, expired, now.AddDays(-45), ipPrefix: "198.51.100.0/24", cancellationToken: Ct);
        IRetentionService retention = fixture.Host.Services.GetRequiredService<IRetentionService>();

        // First run: nothing older than the raw window has a partition of its own yet, so nothing
        // is dropped; the stranded days are given partitions, and prefixes older than a day go.
        RetentionRunResult first = await retention.RunAsync(Ct);
        Assert.Equal(RetentionRunResult.StatusOk, first.Status);
        Assert.False(first.DryRun);
        Assert.Equal(30, first.RawDays);
        Assert.Equal(1, first.IpPrefixDays);
        Assert.True(first.RowsAnonymised >= 2, "both seeded prefixes are older than a day");
        Assert.Null(await Sql.ScalarAsync<string>(Database.DataSource, "SELECT host(ip_prefix) FROM click_events WHERE click_id = $1", [recent], Ct));

        // Second run: the day the expired click was moved into is older than the raw window and
        // is detached and dropped; the recent day stays.
        RetentionRunResult second = await retention.RunAsync(Ct);
        Assert.Equal(RetentionRunResult.StatusOk, second.Status);
        Assert.NotEmpty(second.PartitionsDropped);
        Assert.Equal(0L, await Sql.ScalarAsync<long>(Database.DataSource, "SELECT count(*) FROM click_events WHERE click_id = $1", [expired], Ct));
        Assert.Equal(1L, await Sql.ScalarAsync<long>(Database.DataSource, "SELECT count(*) FROM click_events WHERE click_id = $1", [recent], Ct));

        Assert.Equal(
            2L,
            await Sql.ScalarAsync<long>(
                Database.DataSource,
                "SELECT count(*) FROM analytics_retention_runs WHERE id = ANY($1)",
                [new[] { first.Id, second.Id }],
                Ct));
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-312")]
    public async Task Retention_InDryRun_ReportsWithoutChangingAnything()
    {
        Fixture fixture = await SeedAsync(
            "analytics-retention-dry",
            settings =>
            {
                settings["Dle:Privacy:Retention:IpPrefixDays"] = "1";
                settings["Dle:Privacy:Retention:DryRun"] = "true";
            });
        long linkId = await TestSeed.LinkAsync(Database, fixture.TenantId, fixture.DomainId, "dry", "https://example.com/", cancellationToken: Ct);
        string clickId = fixture.Tag + "-dry";
        await TestSeed.ClickEventAsync(Database, fixture.TenantId, linkId, clickId, DateTimeOffset.UtcNow.AddDays(-2), ipPrefix: "203.0.113.0/24", cancellationToken: Ct);
        IRetentionService retention = fixture.Host.Services.GetRequiredService<IRetentionService>();

        RetentionRunResult result = await retention.RunAsync(Ct);

        Assert.Equal(RetentionRunResult.StatusDryRun, result.Status);
        Assert.True(result.DryRun);
        Assert.Equal(0, result.RowsAnonymised);
        Assert.Empty(result.PartitionsDropped);
        Assert.Equal(
            "203.0.113.0",
            await Sql.ScalarAsync<string>(Database.DataSource, "SELECT host(ip_prefix) FROM click_events WHERE click_id = $1", [clickId], Ct));
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-320")]
    public async Task TenantExport_IsAZipOfNdjsonPerEntity_WithAManifest_ForTheOwnerOnly()
    {
        Fixture fixture = await SeedAsync("analytics-tenant-export");
        Seeded seeded = await SeedEventsAsync(fixture);
        ControlCredentials admin = await ControlCredentials.IssueApiKeyAsync(fixture.Host, Database, fixture.TenantId, "admin", Ct);
        using HttpClient client = fixture.Host.CreateDirectClient();

        using HttpResponseMessage forbidden = await admin.GetAsync(client, "/api/v1/exports/tenant", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using HttpResponseMessage response = await fixture.Key.GetAsync(client, "/api/v1/exports/tenant", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);

        using MemoryStream buffer = new(await response.Content.ReadAsByteArrayAsync(Ct));
        using ZipArchive archive = new(buffer, ZipArchiveMode.Read);
        List<string> names = [.. archive.Entries.Select(static entry => entry.FullName)];
        Assert.Contains("manifest.json", names);
        Assert.Contains("tenant.ndjson", names);
        Assert.Contains("links.ndjson", names);
        Assert.Contains("installs.ndjson", names);
        Assert.Contains("attributions.ndjson", names);

        using Stream manifestStream = archive.GetEntry("manifest.json")!.Open();
        using JsonDocument manifest = await JsonDocument.ParseAsync(manifestStream, cancellationToken: Ct);
        Assert.Equal(fixture.TenantId, manifest.RootElement.GetProperty("tenant_id").GetGuid());
        JsonElement counts = manifest.RootElement.GetProperty("counts");
        Assert.Equal(2, counts.GetProperty("links.ndjson").GetInt64());
        Assert.Equal(1, counts.GetProperty("installs.ndjson").GetInt64());
        Assert.Equal(1, counts.GetProperty("attributions.ndjson").GetInt64());

        using Stream linksStream = archive.GetEntry("links.ndjson")!.Open();
        using StreamReader reader = new(linksStream);
        string links = await reader.ReadToEndAsync(Ct);
        Assert.Contains(seeded.LinkId.ToString(CultureInfo.InvariantCulture), links, StringComparison.Ordinal);
    }

    private static long SumOf(JsonElement rows, string member) =>
        rows.EnumerateArray().Sum(row => row.GetProperty(member).GetInt64());

    /// <summary>
    /// One tenant's worth of traffic: two links, three clicks on the first (iOS, Android and a bot
    /// on iOS) three and two hours ago, one Android install attributed to the iOS click by install
    /// referrer.
    /// </summary>
    private async Task<Seeded> SeedEventsAsync(Fixture fixture)
    {
        long linkId = await TestSeed.LinkAsync(Database, fixture.TenantId, fixture.DomainId, "measured", "https://example.com/", cancellationToken: Ct);
        long otherLinkId = await TestSeed.LinkAsync(Database, fixture.TenantId, fixture.DomainId, "quiet", "https://example.com/quiet", cancellationToken: Ct);
        Guid appId = await TestSeed.AppAsync(Database, fixture.TenantId, fixture.DomainId, "android", "sk.example.measured", cancellationToken: Ct);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset hour = new(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        string tag = fixture.Tag;

        await TestSeed.ClickEventAsync(Database, fixture.TenantId, linkId, tag + "-ios", now.AddHours(-3), osFamily: "iOS", cancellationToken: Ct);
        await TestSeed.ClickEventAsync(Database, fixture.TenantId, linkId, tag + "-android", now.AddHours(-2), osFamily: "Android", cancellationToken: Ct);
        await TestSeed.ClickEventAsync(Database, fixture.TenantId, linkId, tag + "-bot", now.AddHours(-2), isBot: true, osFamily: "iOS", cancellationToken: Ct);

        Guid installRowId = await TestSeed.InstallAsync(Database, fixture.TenantId, appId, tag + "-install", cancellationToken: Ct);
        _ = await TestSeed.AttributionAsync(Database, fixture.TenantId, installRowId, tag + "-ios", linkId, cancellationToken: Ct);

        return new Seeded(linkId, otherLinkId, hour.AddHours(-6), hour.AddHours(-1));
    }

    private async Task<Fixture> SeedAsync(string name, Action<Dictionary<string, string?>>? configure = null)
    {
        string host = TestSeed.UniqueHost(name);
        Guid tenantId = await TestSeed.TenantAsync(Database, name, cancellationToken: Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);
        DleTestHost<DleControlOptions> controlHost = StartControl(configure);
        ControlCredentials key = await ControlCredentials.IssueApiKeyAsync(controlHost, Database, tenantId, "owner", Ct);
        return new Fixture(controlHost, key, tenantId, domainId);
    }

    private sealed record Seeded(long LinkId, long OtherLinkId, DateTimeOffset AlignedFrom, DateTimeOffset AlignedTo);

    private sealed record Fixture(
        DleTestHost<DleControlOptions> Host,
        ControlCredentials Key,
        Guid TenantId,
        Guid DomainId)
    {
        /// <summary>A short token unique to the tenant, so click ids never collide across tests.</summary>
        public string Tag => TenantId.ToString("N")[..8];
    }
}
