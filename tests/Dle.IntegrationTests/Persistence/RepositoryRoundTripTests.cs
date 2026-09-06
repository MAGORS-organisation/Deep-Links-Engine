using System.Text.Json;

using Dle.Domain.Contracts;
using Dle.Domain.Entities;
using Dle.Domain.Links;
using Dle.Domain.Ports;
using Dle.Domain.Privacy;
using Dle.Domain.Routing;
using Dle.Domain.Serialization;
using Dle.Persistence.Repositories;

namespace Dle.IntegrationTests.Persistence;

/// <summary>
/// Every control-plane repository writes a row and reads the same values back (§B.5.2).
/// </summary>
/// <remarks>
/// The interesting half is the columns PostgreSQL types differently from C#: <c>jsonb</c> for the
/// routing rules and the evidence, <c>text[]</c> for tags and scopes, <c>numeric(3,2)</c> for a
/// confidence, <c>citext</c> for a slug, <c>bytea</c> for a hash. A provider that stored all of them
/// as strings would pass a round trip written against itself and fail against the schema — which is
/// why §D.4 rules an in-memory provider out for this suite.
/// </remarks>
public sealed class RepositoryRoundTripTests(DleInfrastructureFixture infrastructure)
    : DleIntegrationTest(infrastructure)
{
    [RequiresDockerFact]
    public async Task TenantRepository_CreatesAndReadsBack_IncludingTheConsentModeAndSettings()
    {
        await using ControlPlaneScope bootstrap = ControlPlaneScope.Open(Database);

        Tenant created = await bootstrap.Resolve<TenantRepository>().CreateAsync(
            new Tenant
            {
                Slug = "acme",
                Name = "ACME",
                Status = "active",
                ConsentMode = "full",
                Settings = """{"theme":"dark"}""",
            },
            Ct);

        Assert.NotEqual(Guid.Empty, created.Id);

        await using ControlPlaneScope scope = ControlPlaneScope.Open(Database, created.Id);
        TenantRepository tenants = scope.Resolve<TenantRepository>();

        Tenant? read = await tenants.GetAsync(created.Id, Ct);

        Assert.NotNull(read);
        Assert.Equal("acme", read.Slug);
        Assert.Equal("full", read.ConsentMode);
        Assert.Equal("""{"theme": "dark"}""", read.Settings);

        // citext again: the slug lookup has to ignore case here too.
        Assert.NotNull(await tenants.FindBySlugAsync("ACME", Ct));
    }

    [RequiresDockerFact]
    public async Task DomainRepository_RoundTripsADomainAndItsVerificationHistory()
    {
        string host = TestSeed.UniqueHost("domain-rt");
        Guid tenantId = await TestSeed.TenantAsync(Database, "domain-rt", cancellationToken: Ct);

        await using ControlPlaneScope scope = ControlPlaneScope.Open(Database, tenantId);
        DomainRepository domains = scope.Resolve<DomainRepository>();

        LinkDomain created = await domains.AddAsync(
            new LinkDomain
            {
                TenantId = tenantId,

                // Mixed case and a leading www., both of which HostNormalizer removes on the way in.
                Host = "WWW." + host.ToUpperInvariant(),
                IsDefault = true,
                ConsentModeOverride = "aggregate_only",
                Branding = """{"default_language":"sk"}""",
            },
            Ct);

        Assert.Equal(host, created.Host);

        LinkDomain? read = await domains.FindByHostAsync(host, Ct);

        Assert.NotNull(read);
        Assert.Equal(created.Id, read.Id);
        Assert.Equal("aggregate_only", read.ConsentModeOverride);

        _ = await domains.RecordVerificationAsync(
            new DomainVerification
            {
                DomainId = created.Id,
                Kind = "aasa",
                Status = "verified",
                HttpStatus = 200,
                RedirectCount = 0,
                Issues = "[]",
            },
            Ct);

        IReadOnlyList<DomainVerification> history = await domains.GetVerificationHistoryAsync(created.Id, 10, Ct);

        Assert.Single(history);
        Assert.Equal("aasa", history[0].Kind);
        Assert.Equal(200, history[0].HttpStatus);

        DomainRuntimeConfig? runtime = await domains.GetRuntimeConfigAsync(host, Ct);

        Assert.NotNull(runtime);
        Assert.Equal(tenantId, runtime.TenantId);
        Assert.Equal(ConsentMode.AggregateOnly, runtime.DomainConsentMode);
    }

    [RequiresDockerFact]
    public async Task AppRepository_RoundTripsTheTextArrayColumnsAndTheDomainPairing()
    {
        string host = TestSeed.UniqueHost("app-rt");
        Guid tenantId = await TestSeed.TenantAsync(Database, "app-rt", cancellationToken: Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);

        await using ControlPlaneScope scope = ControlPlaneScope.Open(Database, tenantId);
        AppRepository apps = scope.Resolve<AppRepository>();

        App created = await apps.AddAsync(
            new App
            {
                TenantId = tenantId,
                Platform = AppRepository.AndroidPlatform,
                BundleId = "sk.example.app",
                CertFingerprints = ["AA:BB", "CC:DD"],
                PlaySigningFingerprints = ["EE:FF"],
                StoreUrl = "https://play.google.com/store/apps/details?id=sk.example.app",
                CustomScheme = "example",
            },
            Ct);

        Assert.True(await apps.AttachDomainAsync(created.Id, domainId, Ct));

        App? read = await apps.GetAsync(created.Id, Ct);

        Assert.NotNull(read);
        Assert.Equal(["AA:BB", "CC:DD"], read.CertFingerprints);
        Assert.Equal(["EE:FF"], read.PlaySigningFingerprints);
        Assert.Equal("example", read.CustomScheme);

        IReadOnlyList<Dle.Domain.WellKnown.AndroidAppEntry> entries = await apps.GetAssetLinkEntriesAsync(host, Ct);

        Assert.Single(entries);
        Assert.Equal("sk.example.app", entries[0].PackageName);

        IReadOnlyList<App> forHost = await apps.ListForHostAsync(host, AppRepository.AndroidPlatform, Ct);
        Assert.Single(forHost);
    }

    [RequiresDockerFact]
    [Trait("Spec", "FR-107")]
    public async Task LinkRepository_RoundTripsALinkWithItsRevisionAndSnapshot()
    {
        string host = TestSeed.UniqueHost("link-rt");
        Guid tenantId = await TestSeed.TenantAsync(Database, "link-rt", "full", Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);

        await using ControlPlaneScope scope = ControlPlaneScope.Open(Database, tenantId);
        LinkRepository links = scope.Resolve<LinkRepository>();

        long id = TestSeed.NextLinkId();

        Link created = await links.AddAsync(
            new Link
            {
                Id = id,
                TenantId = tenantId,
                DomainId = domainId,
                Slug = "SummerSale",
                Title = "Summer sale",
                TargetUrl = "https://example.test/summer",
                DeeplinkPath = "/promo/summer",
                RoutingRules = TestRules.IosAppOrStore("https://apps.apple.com/app/id1?pt=1&ct=summer"),
                OgMeta = JsonSerializer.Serialize(
                    new OgMeta { Title = "Summer", SiteName = "Example" },
                    DleDomainJsonContext.Default.OgMeta),
                Utm = """{"utm_source":"print"}""",
                Tags = ["summer", "print"],
            },
            createdBy: null,
            Ct);

        // Normalised on the way in, which is what makes the printed URL case insensitive.
        Assert.Equal("summersale", created.Slug);
        Assert.Equal(1, created.Version);

        Link? read = await links.GetAsync(id, includeQuarantined: false, Ct);

        Assert.NotNull(read);
        Assert.Equal(["summer", "print"], read.Tags);
        Assert.Equal("/promo/summer", read.DeeplinkPath);

        read.Title = "Summer sale, extended";
        int version = await links.UpdateAsync(read, changedBy: null, "extended by a week", Ct);

        Assert.Equal(2, version);

        IReadOnlyList<LinkVersion> revisions = await links.GetRevisionsAsync(id, 10, Ct);

        Assert.Equal(2, revisions.Count);
        Assert.Equal(2, revisions[0].Version);
        Assert.Equal("extended by a week", revisions[0].ChangeNote);

        LinkSnapshot? snapshot = await links.GetSnapshotAsync(host, "SUMMERSALE", Ct);

        Assert.NotNull(snapshot);
        Assert.Equal(id, snapshot.Id);
        Assert.Equal(ConsentMode.Full, snapshot.TenantConsentMode);
        Assert.Equal(2, snapshot.RoutingRules.Count);
        Assert.Equal(RoutingActionKind.AppOrStore, snapshot.RoutingRules[0].Then.Action);
        Assert.Equal("print", snapshot.Utm["utm_source"]);
    }

    [RequiresDockerFact]
    public async Task ApiKeyRepository_RoundTripsTheHashAndTheScopeArray()
    {
        Guid tenantId = await TestSeed.TenantAsync(Database, "apikey-rt", cancellationToken: Ct);

        await using ControlPlaneScope scope = ControlPlaneScope.Open(Database, tenantId);
        ApiKeyRepository keys = scope.Resolve<ApiKeyRepository>();

        byte[] hash = [1, 2, 3, 4, 5];

        ApiKey created = await keys.AddAsync(
            new ApiKey
            {
                TenantId = tenantId,
                Name = "ci",
                Prefix = "abc12345",
                Hash = hash,
                Role = "editor",
                Scopes = ["links:read", "links:write"],
            },
            Ct);

        Assert.NotEqual(Guid.Empty, created.Id);

        ApiKey? read = await keys.FindByPrefixAsync("abc12345", Ct);

        Assert.NotNull(read);
        Assert.Equal(hash, read.Hash);
        Assert.Equal(["links:read", "links:write"], read.Scopes);
        Assert.Equal("editor", read.Role);
    }

    [RequiresDockerFact]
    public async Task AttributionRepository_RoundTripsTheInstallAndTheAttributionEvidence()
    {
        Guid tenantId = await TestSeed.TenantAsync(Database, "attr-rt", cancellationToken: Ct);
        Guid appId = await TestSeed.AppAsync(Database, tenantId, domainId: null, "android", "sk.example.app", cancellationToken: Ct);

        await using ControlPlaneScope scope = ControlPlaneScope.Open(Database, tenantId);
        AttributionRepository attributions = scope.Resolve<AttributionRepository>();

        Install install = await attributions.GetOrCreateInstallAsync(
            new Install
            {
                TenantId = tenantId,
                AppId = appId,
                InstallId = "install-1",
                Platform = "android",
                AppVersion = "1.2.3",
            },
            Ct);

        // Idempotent by the unique index on (app_id, install_id): a second SDK call returns the row
        // that is already there rather than creating a second installation.
        Install again = await attributions.GetOrCreateInstallAsync(
            new Install
            {
                TenantId = tenantId,
                AppId = appId,
                InstallId = "install-1",
                Platform = "android",
            },
            Ct);

        Assert.Equal(install.Id, again.Id);

        AttributionWriteResult write = await attributions.TryCreateAsync(
            new AttributionRecord
            {
                TenantId = tenantId,
                InstallId = install.Id,
                ClickId = "click-1",
                LinkId = null,
                MatchType = "install_referrer",
                Confidence = 1.00m,
                WindowSeconds = 3600,
                Evidence = """{"strategy":"install_referrer"}""",
            },
            Ct);

        Assert.Equal(AttributionOutcome.Created, write.Outcome);

        AttributionRecord? read = await attributions.FindByInstallAsync(install.Id, Ct);

        Assert.NotNull(read);
        Assert.Equal(1.00m, read.Confidence);
        Assert.Equal(3600, read.WindowSeconds);
        Assert.Equal("""{"strategy": "install_referrer"}""", read.Evidence);
    }

    [RequiresDockerFact]
    public async Task AbuseRepository_RecordsAReportAndQuarantinesTheLink()
    {
        string host = TestSeed.UniqueHost("abuse-rt");
        Guid tenantId = await TestSeed.TenantAsync(Database, "abuse-rt", cancellationToken: Ct);
        Guid domainId = await TestSeed.DomainAsync(Database, tenantId, host, cancellationToken: Ct);
        long linkId = await TestSeed.LinkAsync(Database, tenantId, domainId, "bad", "https://example.test/", cancellationToken: Ct);

        await using ControlPlaneScope scope = ControlPlaneScope.Open(Database, tenantId);
        AbuseRepository abuse = scope.Resolve<AbuseRepository>();

        AbuseReport? report = await abuse.CreateReportAsync(linkId, "phishing", "looks like a bank", [9, 9, 9], Ct);

        Assert.NotNull(report);
        Assert.Equal("phishing", report.Reason);
        Assert.Equal([9, 9, 9], report.ReporterEmailHash);

        Assert.True(await abuse.QuarantineLinkAsync(linkId, Ct));

        // Quarantined, not deleted: the row has to stay so the edge can answer 410 (TC-103).
        Assert.NotNull(
            await Sql.ScalarAsync<DateTime?>(
                Database.DataSource,
                "SELECT quarantined_at FROM links WHERE id = $1",
                [linkId],
                Ct));

        IReadOnlyList<AbuseReport> queue = await abuse.ListForTriageAsync(status: null, 50, Ct);
        Assert.Single(queue);
    }

    [RequiresDockerFact]
    public async Task WebhookRepository_RoundTripsASubscriptionAndEnqueuesADelivery()
    {
        Guid tenantId = await TestSeed.TenantAsync(Database, "webhook-rt", cancellationToken: Ct);

        await using ControlPlaneScope scope = ControlPlaneScope.Open(Database, tenantId);
        WebhookRepository webhooks = scope.Resolve<WebhookRepository>();

        WebhookSubscription subscription = await webhooks.AddSubscriptionAsync(
            new WebhookSubscription
            {
                TenantId = tenantId,
                Url = "https://hooks.example.test/dle",
                SecretEncrypted = [7, 7, 7],
                EventTypes = ["link.created", "attribution.created"],
            },
            Ct);

        Assert.NotEqual(Guid.Empty, subscription.Id);

        await webhooks.EnqueueAsync(tenantId, "link.created", """{"id":"1"}""", Ct);

        IReadOnlyList<WebhookDelivery> deliveries = await webhooks.ListDeliveriesAsync(null, 10, Ct);

        Assert.Single(deliveries);
        Assert.Equal("link.created", deliveries[0].EventType);
        Assert.Equal(WebhookRepository.PendingStatus, deliveries[0].Status);

        IReadOnlyList<WebhookSubscription> read = await webhooks.ListSubscriptionsAsync(onlyActive: true, Ct);

        Assert.Single(read);
        Assert.Equal(["link.created", "attribution.created"], read[0].EventTypes);
        Assert.Equal([7, 7, 7], read[0].SecretEncrypted);
    }

    [RequiresDockerFact]
    [Trait("Spec", "E.6.3")]
    public async Task AuditLogWriter_RoundTripsAnEntryAndItsJsonbMetadata()
    {
        Guid tenantId = await TestSeed.TenantAsync(Database, "audit-rt", cancellationToken: Ct);

        await using ControlPlaneScope scope = ControlPlaneScope.Open(Database, tenantId);
        AuditLogWriter audit = scope.Resolve<AuditLogWriter>();

        AuditLogEntry entry = await audit.WriteAsync(
            "link.created",
            "link",
            "7000000000000001",
            "api_key",
            actorId: null,
            """{"slug":"summer"}""",
            Ct);

        Assert.NotEqual(Guid.Empty, entry.Id);

        IReadOnlyList<AuditLogEntry> read = await audit.ReadAsync("link", "7000000000000001", 10, Ct);

        Assert.Single(read);
        Assert.Equal("link.created", read[0].Action);
        Assert.Equal("""{"slug": "summer"}""", read[0].Metadata);
    }

    [RequiresDockerFact]
    [Trait("Spec", "ADR-007")]
    public async Task SlugSequenceAllocator_HandsOutStrictlyIncreasingValuesAcrossBlocks()
    {
        Guid tenantId = await TestSeed.TenantAsync(Database, "slug-seq", cancellationToken: Ct);

        await using ControlPlaneScope scope = ControlPlaneScope.Open(Database, tenantId);
        SlugSequenceAllocator allocator = scope.Resolve<SlugSequenceAllocator>();

        List<long> values = [];

        for (int i = 0; i < 32; i++)
        {
            values.Add(await allocator.NextAsync(Ct));
        }

        Assert.Equal(values.Count, values.Distinct().Count());
        Assert.Equal(values.Order(), values);
    }

    [RequiresDockerFact]
    [Trait("Spec", "B.7.3")]
    public async Task IdempotencyStore_ReplaysAStoredResponseForTheSameKey()
    {
        Guid tenantId = await TestSeed.TenantAsync(Database, "idem-rt", cancellationToken: Ct);

        await using ControlPlaneScope scope = ControlPlaneScope.Open(Database, tenantId);
        IdempotencyStore store = scope.Resolve<IdempotencyStore>();

        byte[] requestHash = [1, 1, 2, 3, 5];

        IdempotencyLookup first = await store.BeginAsync("key-1", "POST /api/v1/links", requestHash, Ct);

        Assert.Equal(IdempotencyOutcome.Proceed, first.Outcome);

        Assert.Equal(1, await store.CompleteAsync("key-1", "POST /api/v1/links", 201, """{"id":"1"}""", Ct));

        IdempotencyLookup replay = await store.BeginAsync("key-1", "POST /api/v1/links", requestHash, Ct);

        Assert.Equal(IdempotencyOutcome.Replay, replay.Outcome);
        Assert.Equal(201, replay.ResponseStatus);
        Assert.Equal("""{"id":"1"}""", replay.ResponseBody);
    }
}
