# Configuration reference

**What this is:** every `Dle:*` key the two hosts read, with type, default, effect and when you would change it — taken from `src/Dle.Edge/appsettings.json` and `src/Dle.Control/appsettings.json`, which are the inventories, not a list of overrides.
**Who it is for:** the operator writing a `.env`, a Helm `values.yaml` or a secret manifest.

## How configuration reaches the process

- **Environment variables** — the colon becomes a double underscore: `Dle:Privacy:ConsentMode` → `Dle__Privacy__ConsentMode`; lists use an index: `Dle__Edge__Network__KnownProxies__0=10.0.0.5`. Connection strings: `ConnectionStrings__Postgres`.
- **Compose** — `deploy/.env.example` exposes the common ones as `DLE_*` shortcuts and maps them in `docker-compose.yml`; anything else goes in as `Dle__…` directly.
- **Helm** — `edge.config.*`, `control.config.*`, `secrets.*` in `deploy/helm/dle/values.yaml`.
- Options are validated at start-up (`ValidateOnStart`, [§C.4](../zadanie.md#c4-konfigurácia)): an empty master secret or an out-of-range value stops the process rather than running degraded.
- Both hosts share the `Dle:Privacy` and `Dle:Crypto` shapes; keep their values identical across edge and control.

Types: `bool`, `int`, `double`, `string`, `enum` (allowed values listed), `list`.

## Connection strings (both hosts)

| Key | Type | Default | Effect | Change when |
|---|---|---|---|---|
| `ConnectionStrings:Postgres` | string | edge: empty (required); control: local dev string | Primary database. The edge only reads through it, and only on a cache miss | Always, in production |
| `ConnectionStrings:PostgresRead` | string | empty | Optional read replica for the edge (Profile B). Empty → use `Postgres` | Profile B with replicas |
| `ConnectionStrings:Valkey` | string | empty | Shared L2 cache. Empty → HybridCache runs L1-only per instance | More than one edge instance (needed for coherent invalidation) |
| `ConnectionStrings:ClickHouse` | string | empty | Analytics sink when `Dle:Analytics:Provider=clickhouse` | Large volumes (ADR-006) |

## Edge (`src/Dle.Edge/appsettings.json`)

### `Dle:Edge`

| Key | Type | Default | Effect | Change when |
|---|---|---|---|---|
| `UserAgentCacheCapacity` | int | 20000 | Distinct UA strings the classifier memoises; bounded because the key space is attacker-controlled | Very high UA diversity with memory to spare |
| `PreviewQueryKey` / `PreviewQueryValue` | string | `_dl` / `preview` | `?_dl=preview` runs the whole pipeline and records no click (FR-166) | A collision with your own query parameters |
| `Interstitial:Enabled` | bool | true | Serve the interstitial page (in-app webviews, app-not-installed). Off → always redirect | Never in production: webviews only fire a Universal Link on a real tap ([§A.2.6](../zadanie.md#a26-in-app-prehliadače-a-crawlery)) |
| `Interstitial:AutoRedirectMs` | int | 1200 | Delay before the fallback fires. Below ~500 ms the store opens before the OS can hand the link to an installed app | Rarely; measure on devices first |
| `Interstitial:Branding` | enum `None` \| `Instance` \| `Tenant` | `Tenant` | Whose name/logo the page shows | Single-tenant instance → `Instance` |
| `Interstitial:ProductName`, `LogoUrl`, `SupportUrl`, `PrivacyUrl` | string | empty | Instance branding and the links every interstitial shows | Set for production |
| `Interstitial:AppealUrl`, `AppealEmail` | string | empty | Shown on the `410` page of a quarantined link ([§E.3](../zadanie.md#e3-ochrana-proti-zneužitiu-redirektora), TC-103) | Set for production — DSA art. 16 expects a contact |
| `Interstitial:ShowClaimCode` | bool | true | Show the six-character claim code on iOS interstitials (deterministic deferred path, ADR-008) | Off only if your iOS app does not implement claim codes |
| `Interstitial:DefaultLanguage` | enum `en` \| `sk` | `en` | Fallback language (NFR-15) | Slovak-first deployments |
| `BotDetection:ReverseDnsVerify` | bool | true | Verify a claimed crawler by reverse DNS; off makes `is_bot` a claim, not a fact (FR-161, TC-107) | Closed test networks only |
| `BotDetection:CacheTtlMinutes` / `CacheCapacity` / `VerificationTimeoutMs` | int | 60 / 20000 / 750 | Reverse-DNS result cache and timeout | DNS resolver latency issues |
| `GeoIp:Provider` | enum `MaxMindMmap` \| `None` | `MaxMindMmap` | Offline country lookup; the file is never downloaded by the edge (NFR-14) | `None` if you do not need geo rules |
| `GeoIp:Path` | string | empty | Path to the `.mmdb`. Empty → country stays null, geo rules fall through to the default rule, `dle_geoip_available=0` | Set when you have the file (`DLE_GEOIP_PATH` in compose) |
| `GeoIp:AutoUpdate` / `RefreshMinutes` | bool / int | true / 60 | Re-open the file when it changes on disk | Your update job writes elsewhere |
| `Cache:L1Seconds` / `L2Minutes` | int | 30 / 10 | In-process and Valkey TTLs for link snapshots | Trade freshness against DB load |
| `Cache:NegativeSeconds` | int | 15 | How long a miss is cached; must not outlive the creation of a link someone is about to publish | Shorten if operators create-and-share within seconds |
| `Security:Enabled` | bool | true | Security headers (HSTS, CSP, frame options) on every edge response | Never off in production |
| `Security:HstsMaxAgeSeconds` / `HstsIncludeSubDomains` | int / bool | 31536000 / true | HSTS policy | Sub-domains that must stay on plain HTTP (they should not) |
| `Security:ImageSources` | string | `https: data:` | CSP `img-src` for OG images on the interstitial | Restrict to your CDN |
| `Network:UseForwardedHeaders` | bool | false | Honour `X-Forwarded-*` — only behind a proxy named below. The client address is the rate-limit key, the click identifier and the GeoIP key; an honoured header from an untrusted peer lets a client choose all three | Behind Caddy / Ingress: true (compose does this) |
| `Network:ForwardLimit` | int | 1 | Proxy hops to trust | +1 per CDN/WAF layer in front |
| `Network:KnownProxies` / `KnownNetworks` | list | empty | Which peers may set forwarded headers | The proxy's IP or subnet — never `0.0.0.0/0` |

### `Dle:Persistence:Fast` (edge)

| Key | Type | Default | Effect | Change when |
|---|---|---|---|---|
| `ApplicationName` | string | `dle-edge` | Shown in `pg_stat_activity` | Multiple edge pools |
| `MaxPoolSize` / `MinPoolSize` | int | 64 / 4 | Npgsql pool per instance | Per your PostgreSQL `max_connections` budget |
| `CommandTimeoutSeconds` | int | 5 | Lookup timeout; beyond it the edge treats the DB as unavailable and serves cache or `503` | Slow replicas |
| `WriteCommandTimeoutSeconds` | int | 30 | Batch `COPY` timeout for click events | Very large batches |
| `ClickEventChannelCapacity` | int | 100000 | Bounded channel; when full, events are **dropped** (counted in `dle_click_events_dropped_total`) rather than delaying the redirect (NFR-06) | Sustained bursts; watch memory |
| `ClickEventBatchSize` / `ClickEventFlushMilliseconds` | int | 5000 / 250 | Batch writer cadence | Latency vs. throughput of analytics |
| `ShutdownFlushSeconds` | int | 10 | Grace period to flush the channel on stop | Match the orchestrator's termination grace |

### `Dle:RateLimits:Edge`

Defaults from [§E.9](../zadanie.md#e9-rate-limity-a-kvóty--konkrétne-hodnoty). `Enabled=false` switches all edge limits off — test environments only.

| Key | Type | Default | Effect |
|---|---|---|---|
| `Resolve:PermitsPerWindow` / `WindowSeconds` / `SegmentsPerWindow` | int | 600 / 60 / 6 | Sliding window for successful `GET /{slug}`, keyed by IPv4 /24 or IPv6 /48 |
| `NotFound:TokensPerPeriod` / `ReplenishmentPeriodSeconds` / `Burst` | int | 20 / 60 / 40 | Token bucket for `404` responses — a **separate** partition from `Resolve` (T-07) |
| `NotFound:ShadowBanMinutes` | int | 15 | After the bucket is empty the prefix gets `404` without a DB query |
| `NotFound:TrackedPrefixCapacity` | int | 50000 | Prefixes tracked at once |
| `Qr:PermitsPerWindow` / `WindowSeconds` / `SegmentsPerWindow` | int | 30 / 60 / 6 | `GET /{slug}/qr` per address |

### `Dle:Telemetry` (edge)

| Key | Type | Default | Effect |
|---|---|---|---|
| `ServiceName` / `ServiceNamespace` | string | `dle-edge` / `dle` | OTel resource attributes |
| `OtlpEndpoint` | string | empty | OTLP exporter target; empty → collected in-process only |
| `TraceSampleRatio` | double | 0.05 | Sample 5 % of resolve traces |
| `InstrumentHttp` / `InstrumentRuntime` | bool | true / true | ASP.NET Core and runtime instrumentation |

## Control (`src/Dle.Control/appsettings.json`)

### `Dle:Control`

| Key | Type | Default | Effect | Change when |
|---|---|---|---|---|
| `PublicScheme` | enum `https` \| `http` | `https` | Scheme used to build `short_url`, `qr_url`, store URLs | `http` only in local dev (the compose override does it) |
| `QrPathSuffix` | string | `/qr` | Suffix appended to a short URL for the QR endpoint | Never, unless the edge route is changed too |
| `DefaultPageSize` / `MaxPageSize` | int | 50 / 200 | List paging | Heavy API consumers |
| `BulkMaxRows` | int | 10000 | Rows per `POST /links/bulk` stream | Larger imports (also raise `WriteCommandTimeoutSeconds`) |
| `AssociationPropagationDays` | int | 7 | Value shown in `propagation_notice` and the console banner after AASA/assetlinks changes ([domains.md](domains.md)) | Do not lower it — the platforms do not go faster |
| `MaxLinkVersions` | int | 50 | Revisions kept per link | Audit requirements |
| `NodeId` | int | 0 | Node discriminator for slug sequences when several control instances allocate | Each control replica gets its own value (Helm does this) |
| `InstanceTenantId` | GUID | null | The operating tenant that may create other tenants | Multi-tenant instances |
| `AllowTenantSelfService` | bool | false | Any authenticated OIDC user may create a tenant. With neither this nor `InstanceTenantId` set, tenant routes deny everyone | Single-organisation first run — then turn it off ([deploy/README — First credential](../../deploy/README.md#first-credential)) |
| `ServeAdminSpa` / `SpaIndexFile` | bool / string | true / `index.html` | Serve the admin console from the control host under `/admin/` | Console hosted elsewhere |

### `Dle:Persistence` (control)

| Key | Type | Default | Effect | Change when |
|---|---|---|---|---|
| `CommandTimeoutSeconds` | int | 30 | EF Core command timeout | Long analytics queries |
| `MaxRetryCount` / `MaxRetryDelaySeconds` | int | 3 / 5 | Npgsql transient-failure retries | Flaky network to the DB |
| `SlugBlockSize` | int | 1000 | Sequence values reserved per allocation (ADR-007) | Very high link creation rates |
| `SlugSequenceName` | string | `default` | Named sequence to permute | Never after go-live — slugs derive from it |
| `IdempotencyRetentionHours` | int | 24 | How long an `Idempotency-Key` replay is honoured ([api.md](../integration/api.md#idempotency-key)) | Clients that retry later than a day |
| `WebhookLeaseSeconds` | int | 60 | Outbox lease so two control replicas do not deliver the same event | Slow receivers with long timeouts |

### `Dle:Identity`

| Key | Type | Default | Effect | Change when |
|---|---|---|---|---|
| `CredentialCacheSeconds` | int | 60 | How long a verified API key / SDK key is cached; revocation takes effect within this window | Stricter revocation latency |
| `EnableSdkKeys` | bool | true | Accept SDK keys on `/v1/*` | Never off in production |
| `SdkKeyName` | string | `dlk` | Prefix of SDK keys | Cosmetic |
| `Oidc:Authority` / `ClientId` / `Audience` | string | empty | The OIDC provider the console and human callers authenticate against | Required for the first credential |
| `Oidc:TenantClaim` / `RoleClaim` | string | `dle_tenant` / `dle_role` | Claim names read from the token | Your provider maps claims differently |
| `Oidc:RequireHttpsMetadata` | bool | true | Refuse a plain-HTTP discovery document | `localhost` only |

### `Dle:Crypto` (both hosts — keep identical)

| Key | Type | Default | Effect | Change when |
|---|---|---|---|---|
| `MasterSecret` | string | empty (**required**, ≥ 32 chars) | Derives the slug permutation key, the IP salt and the wrapping of stored signing keys. Supply as `Dle__Crypto__MasterSecret`; never commit | Set once; changing it changes every slug and invalidates every click token |
| `SigningAlgorithm` | enum `HS256` \| `Ed25519` \| … | control `Ed25519`, edge `HS256` | Algorithm the signer uses (verifiers accept a set — [§E.4.2](../zadanie.md#e42-formát-podpísaného-tokenu)). The edge signs click tokens (HMAC); the control plane signs webhooks and tokens (Ed25519) | Phase 1 PQ migration |
| `HybridPqEnabled` | bool | false | Reserved: `Ed25519+MLDSA65` composite signing (v2) | Not before the platforms leave `SYSLIB5006` |
| `KeyRotationDays` | int | 90 | Signing-key rotation cadence (K2, K4, T-15) | Policy |
| `SlugFeistelRounds` | int | 4 | Rounds of the keyed permutation (ADR-007) | Never after go-live |
| `ApiKeyPrefix` | string | `dle` | Prefix of API keys (control) | Cosmetic |
| `IpSaltRotationHours` | int | 24 | Daily salt for IP hashing (K9) | Shorter for stricter unlinkability |

### `Dle:Attribution` (control)

| Key | Type | Default | Effect | Change when |
|---|---|---|---|---|
| `Strategies` | list | `install_referrer, login, claim_code` | Deterministic strategies in evaluation order; `probabilistic` is **not** in the default | Add `probabilistic` only together with the block below |
| `Probabilistic:Enabled` | bool | false | The opt-in module | Only with a documented legal basis ([compliance/privacy.md](../compliance/privacy.md)) |
| `Probabilistic:WindowMinutes` | int | 60 | Match window — 60, not 7 days ([§A.2.5](../zadanie.md#a25-presnosť-probabilistického-párovania--čísla)) | Do not raise it |
| `Probabilistic:MinConfidence` | double | 0.55 | Below this the result is `none` | Precision vs. recall |
| `Probabilistic:RequireConsent` | bool | true | Refuse signals without `consent.attribution=true` | Never false in the EU |
| `ClaimCode:Enabled` / `TtlMinutes` | bool / int | true / 15 | The iOS deterministic path (K3) | Longer TTL for slow store installs; the code is 30 random bits, so keep it short |

### `Dle:Privacy` (both hosts — keep identical)

| Key | Type | Default | Effect | Change when |
|---|---|---|---|---|
| `ConsentMode` | enum `off` \| `aggregate_only` \| `full` | `aggregate_only` | Tenant default and the ceiling a tenant may choose ([§E.6.2](../zadanie.md#e62-tri-režimy-prevádzky-produktová-funkcia-nie-prepínač-v-kóde)) | `full` only with a consent flow in place |
| `IpStorage` | enum `none` \| `hash_only` \| `prefix` \| `full` | `hash_only` | Deployment ceiling on what may be derived from an address | `none` for the strictest posture; `prefix` only for the probabilistic module |
| `IpSaltRotationHours` | int | 24 | Same as `Crypto:IpSaltRotationHours` | Keep equal |
| `Retention:RawDays` | int | control 30, **edge 90** | Days of hashed raw events. The two shipped files differ — set both to your policy value | Policy |
| `Retention:AggregatedDays` | int | 730 | Days of aggregates | Policy |

### `Dle:Analytics`, `Dle:Abuse`, `Dle:Webhooks` (control)

| Key | Type | Default | Effect | Change when |
|---|---|---|---|---|
| `Analytics:Provider` | enum `postgres` \| `clickhouse` | `postgres` | Where rollups and queries run (ADR-006) | > ~10⁸ events/month |
| `Abuse:UrlHausEnabled` | bool | false | Check targets against URLhaus (outbound from control only) | On, for any public-facing instance |
| `Abuse:BlocklistPath` | string | empty | Local blocklist file (hosts / URLs) | Corporate blocklists |
| `Abuse:RecheckIntervalHours` | int | 24 | Nightly re-check of active targets ([§E.3](../zadanie.md#e3-ochrana-proti-zneužitiu-redirektora) step 5) | — |
| `Abuse:ReportsPerHourPerIp` | int | 5 | `POST /abuse-reports` limit | — |
| `Webhooks:MaxAttempts` / `BaseDelaySeconds` / `TimeoutSeconds` | int | 8 / 5 / 10 | Retry policy ([webhooks.md](../integration/webhooks.md#retries-backoff-dead-letter-queue)); further keys (`MaxDelaySeconds` 3600, `JitterRatio` 0.2, `ConnectTimeoutSeconds` 5, `SignatureToleranceMinutes` 5, `BatchSize` 50, `PollIntervalSeconds` 5, `MaxSubscriptionsPerTenant` 20, `AllowPrivateDestinations` false) take their code defaults | Slow receivers |

### `Dle:RateLimits` (control)

| Key | Type | Default | Effect |
|---|---|---|---|
| `LinkCreatePerMinute` | int | 60 | `POST /api/v1/links` per API key |
| `NewTenantLinkCreatePerMinute` / `NewTenantDays` | int | 10 / 7 | Lower limit for a tenant's first week (T-01) |
| `BulkConcurrentBatches` / `BulkQueuedBatches` | int | 2 / 0 | Concurrent bulk streams per tenant; queued beyond that |
| `AuthAttemptsPerMinute` / `AuthAttemptBurst` | int | 10 / 10 | Credential checks per IP + identifier (T-17) |
| `ReadPerMinute` / `WritePerMinute` | int | 600 / 120 | General `/api/v1` budgets per key |
| `RetryAfterSeconds` | int | 60 | Value of the `Retry-After` header on `429` |

### `Dle:Telemetry` (control)

| Key | Type | Default | Effect |
|---|---|---|---|
| `ServiceName` | string | `dle-control` | OTel resource |
| `OtlpEndpoint` | string | empty | OTLP target |
| `ConsoleLogging` | bool | true | Structured console logs (what compose/Kubernetes collect) |
| `TraceSampleRatio` | double | 1.0 | Control traffic is low — sample everything |
| `TraceRequests` | bool | true | ASP.NET Core request spans |

## A production baseline

```bash
Dle__Crypto__MasterSecret=<openssl rand -base64 48>
ConnectionStrings__Postgres=Host=db;Database=dle;Username=dle;Password=…;SSL Mode=Require
ConnectionStrings__Valkey=valkey:6379
Dle__Edge__Network__UseForwardedHeaders=true
Dle__Edge__Network__KnownNetworks__0=10.42.0.0/16          # the proxy's network, nothing wider
Dle__Edge__GeoIp__Path=/geoip/GeoLite2-Country.mmdb
Dle__Edge__Interstitial__ProductName=…  Dle__Edge__Interstitial__AppealEmail=abuse@…
Dle__Privacy__ConsentMode=aggregate_only  Dle__Privacy__IpStorage=hash_only  Dle__Privacy__Retention__RawDays=30
Dle__Identity__Oidc__Authority=https://id.example.com/realms/dle  Dle__Identity__Oidc__ClientId=dle-console  Dle__Identity__Oidc__Audience=dle
Dle__Control__InstanceTenantId=<uuid>                       # or AllowTenantSelfService=true for the first run only
Dle__Abuse__UrlHausEnabled=true
Dle__Telemetry__OtlpEndpoint=http://otel-collector:4317
```

Feature flags for the risky modules (probabilistic matching, ClickHouse sink, PQC signing) are ordinary options and take effect on restart; none requires a redeploy of a different image ([§C.4](../zadanie.md#c4-konfigurácia)).
