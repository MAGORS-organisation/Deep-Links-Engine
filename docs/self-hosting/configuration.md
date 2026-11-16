# Configuration reference

**What this is:** every `Dle:*` key the two hosts read, with type, default, effect and when you would change it — taken from `src/Dle.Edge/appsettings.json` and `src/Dle.Control/appsettings.json`, which are the inventories, not a list of overrides.
**Who it is for:** the operator writing a `.env`, a Helm `values.yaml` or a secret manifest.

## How configuration reaches the process

- **Environment variables** — the colon becomes a double underscore: `Dle:Privacy:ConsentMode` → `Dle__Privacy__ConsentMode`; lists use an index: `Dle__Edge__Network__KnownProxies__0=10.0.0.5`. Connection strings: `ConnectionStrings__Postgres`.
- **Compose** — `deploy/.env.example` exposes the common ones as `DLE_*` shortcuts and maps them in `docker-compose.yml`; anything else goes in as `Dle__…` directly.
- **Helm** — `edge.config.*`, `control.config.*`, `secrets.*` in `deploy/helm/dle/values.yaml`.
- Options are validated at start-up (`ValidateOnStart`, [§C.4](../zadanie.md#c4-konfigurácia)): an empty master secret or an out-of-range value stops the process rather than running degraded.
- Both hosts share the `Dle:Privacy` and `Dle:Crypto` shapes. Keep `Dle:Privacy` identical across edge and control; `Dle:Crypto` differs by design (see [`Dle:Crypto`](#dlecrypto-both-hosts--different-secrets-see-below)).

Types: `bool`, `int`, `double`, `string`, `enum` (allowed values listed), `list`.

## Connection strings (both hosts)

| Key | Type | Default | Effect | Change when |
|---|---|---|---|---|
| `ConnectionStrings:Postgres` | string | edge: empty (required); control: local dev string | Primary database. The edge reads links through it only on a cache miss, and writes click events to it in batches | Always, in production |
| `ConnectionStrings:PostgresRead` | string | empty | Optional read replica for the edge (Profile B). Empty → use `Postgres` | Profile B with replicas |
| `ConnectionStrings:Valkey` | string | empty | Shared L2 cache. Empty → HybridCache runs L1-only per instance | More than one edge instance (needed for coherent invalidation) |
| `ConnectionStrings:ClickHouse` | string | empty | Analytics sink when `Dle:Analytics:Provider=clickhouse` — an experimental provider that is not wired end to end (see `Analytics:Provider` below) | Not yet; do not enable ([README — Known gaps](../../README.md#known-gaps)) |

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
| `Interstitial:ShowClaimCode` | bool | true | Show the six-character claim code on iOS store-bound interstitials (deterministic deferred path, ADR-008). A code is issued, stored and shown only when the tenant's effective consent mode is `full` — the only mode in which it can be redeemed — and only once its row is written. Edge-issued codes are valid for one hour (the page says so) | Off only if your iOS app does not implement claim codes |
| `Interstitial:DefaultLanguage` | enum `en` \| `sk` | `en` | Fallback language (NFR-15) | Slovak-first deployments |
| `BotDetection:ReverseDnsVerify` | bool | true | Verify a claimed crawler by reverse DNS; off makes `is_bot` a claim, not a fact (FR-161, TC-107) | Closed test networks only |
| `BotDetection:CacheTtlMinutes` / `CacheCapacity` / `VerificationTimeoutMs` | int | 60 / 20000 / 750 | Reverse-DNS result cache and timeout | DNS resolver latency issues |
| `GeoIp:Provider` | enum `MaxMindMmap` \| `None` | `MaxMindMmap` | Offline country lookup; the file is never downloaded by the edge (NFR-14) | `None` if you do not need geo rules |
| `GeoIp:Path` | string | empty | Path to the `.mmdb`. Empty → country stays null, geo rules fall through to the default rule, `dle_geoip_available=0` | Set when you have the file (`DLE_GEOIP_PATH` in compose) |
| `GeoIp:AutoUpdate` / `RefreshMinutes` | bool / int | true / 60 | Re-open the file when it changes on disk | Your update job writes elsewhere |
| `Cache:L1Seconds` / `L2Minutes` | int | 30 / 10 | In-process and Valkey TTLs for link snapshots. Link changes and quarantine remove the Valkey entry and publish an invalidation that every edge applies to its in-process cache; if that message is lost (Valkey down), an edge serves the old snapshot for up to `L1Seconds` | Trade freshness against DB load |
| `Cache:NegativeSeconds` | int | 15 | How long a miss is cached; must not outlive the creation of a link someone is about to publish | Shorten if operators create-and-share within seconds |
| `Security:Enabled` | bool | true | Security headers (HSTS, CSP, frame options) on every edge response | Never off in production |
| `Security:HstsMaxAgeSeconds` / `HstsIncludeSubDomains` | int / bool | 31536000 / true | HSTS policy | Sub-domains that must stay on plain HTTP (they should not) |
| `Network:UseForwardedHeaders` | bool | false | Honour `X-Forwarded-*` — only behind a proxy named below. The client address is the rate-limit key, the click identifier and the GeoIP key; an honoured header from an untrusted peer lets a client choose all three | Behind Caddy / Ingress: true (compose does this) |
| `Network:ForwardLimit` | int | 1 | Proxy hops to trust | +1 per CDN/WAF layer in front |
| `Network:KnownProxies` / `KnownNetworks` | list | empty | Which peers may set forwarded headers. Required once `UseForwardedHeaders` is true: with no entry, or one that does not parse, the process refuses to start, because an empty list would mean "trust every peer" | The proxy's IP or subnet — never `0.0.0.0/0` |

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
| `ExposePrometheusMetrics` | bool | false | Serves `GET /metrics` on the service port (the Helm chart turns it on with its ServiceMonitor) |
| `PrometheusScrapeToken` | string | empty | When set, `/metrics` requires `Authorization: Bearer <token>`; set it whenever an ingress can reach the edge |

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
| `NodeId` | int | unset | Node discriminator (0–1023) in the identifiers of new links. Unset, it is derived from the host name, so replicas differ without configuration | Set one value per replica only if you want to rule out the 1-in-1024 chance of two host names hashing alike |
| `InstanceTenantId` | GUID | null | The operating tenant whose owners may create other tenants. When unset, the tenant the bootstrap provisioned (slug `Dle:Bootstrap:DefaultTenantSlug`) is the operator, rediscovered on every start | Multi-tenant instances that were not bootstrapped |
| `AllowTenantSelfService` | bool | false | Any authenticated caller that carries a tenant may create a tenant | Development only — turn it off for anything shared |
| `ServeAdminSpa` / `SpaIndexFile` | bool / string | true / `index.html` | Serve the admin console from the control host under `/admin/`, with its own Content-Security-Policy | Console hosted elsewhere |
| `AdminCorsOrigins` | list | empty | Browser origins allowed to call `/api/*` cross-origin. The bundled console is same-origin and needs none; the SDK routes under `/v1` allow any origin regardless | A console served from another origin, e.g. a developer's `http://localhost:5173` |
| `Network:UseForwardedHeaders` / `ForwardLimit` / `KnownProxies` / `KnownNetworks` | bool / int / list / list | false / 1 / empty / empty | The same four settings as `Dle:Edge:Network`, for the control plane. Behind a proxy the client address is otherwise the proxy's, so the abuse-report and failed-authentication limiters share one bucket across every client and probabilistic matching compares the wrong address | Behind Caddy / Ingress: true, with the same proxy subnet as the edge (compose and the Helm chart do this) |

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
| `Oidc:Authority` / `ClientId` / `Audience` | string | empty | The OIDC provider human callers authenticate against. The admin console supports native PKCE SSO login and pasted API keys | Meant for enterprise single sign-on ([deploy/README — First credential](../../deploy/README.md#first-credential-and-onboarding)) |
| `Oidc:TenantClaim` / `RoleClaim` | string | `dle_tenant` / `dle_role` | The only two claims read from the token. The tenant claim holds a tenant id or slug; the role claim one of `owner`, `admin`, `editor`, `viewer` (the highest wins when several are sent; anything else grants nothing). `dle:*` claims the token brings are discarded | Your provider names claims differently |
| `Oidc:DefaultTenant` | string | empty | Tenant id or slug for a token **without** the tenant claim. Empty: such a token establishes no tenant and is refused. Never implied from `InstanceTenantId` | Single-organisation deployment whose provider cannot emit a tenant claim |
| `Oidc:RequireHttpsMetadata` | bool | true | Refuse a plain-HTTP discovery document | `localhost` only |

### `Dle:Bootstrap` (control)

| Key | Type | Default | Effect | Change when |
|---|---|---|---|---|
| `Enabled` | bool | true | Provisions a root tenant and an owner API key when the database contains no tenants, and on every start reports that tenant as the operator tenant (unless `Dle:Control:InstanceTenantId` is set). The owner key is the one in `InitialApiKey`; without it nothing is provisioned and an error says what to set. Of several replicas starting together exactly one provisions | Set false if tenants are provisioned strictly via custom scripts (then set `InstanceTenantId`) |
| `DefaultTenantSlug` | string | `default` | Slug of the bootstrapped root tenant | Custom initial tenant slug |
| `DefaultTenantName` | string | `Default Tenant` | Display name of the bootstrapped root tenant | Custom organization name |
| `InitialKeyName` | string | `root-bootstrap` | Name given to the initial owner API key | Custom credential description |
| `InitialApiKey` | string | null | The first owner key, generated by the operator: `dle_` (the `Dle:Crypto:ApiKeyPrefix` name), 8 and 43 characters from A-Z a-z 0-9. `DLE_BOOTSTRAP_INITIAL_API_KEY` with Compose, `secrets.bootstrapOwnerKey` in the Helm chart. Never printed or logged; a malformed value provisions nothing | Required on the first start; unused once `root-bootstrap` is revoked |
| `ExportKeyPath` | string | null | Only when `InitialApiKey` is empty: the engine mints the key and writes it to this file (owner-only on Unix). Compose and the Helm chart run with a read-only root filesystem, so only a path under `/tmp` (emptied on restart) or on a volume you mount writes | Automation that cannot supply a key |
| `AllowConsoleSecretOutput` | bool | false | Only when `InitialApiKey` is empty: prints the minted key once to standard output under `DLE BOOTSTRAP OWNER KEY` (never to the structured log), also as the fallback when `ExportKeyPath` cannot be written | Local experiments only — container output is often kept |

### `Dle:Crypto` (both hosts — different secrets, see below)

The two hosts do **not** share one secret. The control plane's `MasterSecret` is `DLE_MASTER_SECRET`. The
edge's `MasterSecret` is `DLE_EDGE_SECRET`, and the control plane receives that same value as
`ClickIdSecret`, `IpHashSecret` and `ClaimCodeSecret` — the three keys both sides must agree on. The
internet-facing edge therefore holds nothing from which the slug permutation or the webhook and token
signing keys derive. Compose and the Helm chart wire this for you.

| Key | Type | Default | Effect | Change when |
|---|---|---|---|---|
| `MasterSecret` | string | empty (**required**, ≥ 32 chars) | Root of every derived key on that host (HKDF-SHA256 with a per-purpose label). On the control plane: slug permutation, webhook Ed25519 signing key, encryption of stored webhook secrets, token key ring. On the edge (where it is the edge secret): click-id, IP-hash and claim-code keys | Never casually: rotation is not implemented, and changing the control plane's value changes the slug permutation ([backup-restore.md](backup-restore.md)) |
| `ClickIdSecret` / `IpHashSecret` / `ClaimCodeSecret` | string | empty (falls back to `MasterSecret`) | Per-purpose override of the root for that one key. The control plane sets all three to the edge secret. Changing them stops outstanding click ids and claim codes from verifying and breaks the continuity of IP hashes; it does not touch slugs or webhook keys | Only through the deployment wiring above |
| `SlugSecret` | string | empty (falls back to `MasterSecret`) | Control plane only: pins the key of the slug permutation, so that changing `MasterSecret` does not change which slugs are generated next | When the master secret has to change and generated slugs must keep their sequence |
| `SigningAlgorithm` | enum `HS256` \| `Ed25519` \| … | control `Ed25519`, edge `HS256` | Algorithm of the token key ring whose public keys `/.well-known/jwks.json` publishes (verifiers accept a set — [§E.4.2](../zadanie.md#e42-formát-podpísaného-tokenu)). Nothing signs with that ring today: the edge's click id is a truncated HMAC that carries no `alg` or `kid`, and webhooks are signed with an Ed25519 key derived from the master secret whatever this says | Phase 1 PQ migration |
| `HybridPqEnabled` | bool | false | Allows the token key ring to use the post-quantum algorithms (`ML-DSA-65`, composite `Ed25519+ML-DSA-65`); webhooks stay HS256+Ed25519 | Not before the platforms leave `SYSLIB5006` |
| `KeyRotationDays` | int | 90 | Meant as the signing-key rotation cadence (K2, K4, T-15). Read only by a rotation routine that nothing calls: signing keys do not rotate today, and the webhook key derived from the master secret never does ([README — Known gaps](../../README.md#known-gaps)) | No effect today |
| `SlugFeistelRounds` | int | 4 | Rounds of the keyed permutation (ADR-007) | Never after go-live |
| `ApiKeyPrefix` | string | `dle` | Prefix of API keys (control) | Cosmetic |
| `IpSaltRotationHours` | int | 24 | Period of the IP-hash salt (K9). The salt is an HMAC of the period number under a key derived from the edge secret, so anyone holding that secret can recompute every past salt: rotation limits linking for others, not for the operator | Shorter narrows the window in which two hashes match; it does not make hashes unlinkable for whoever holds the master secret |

### `Dle:Attribution` (control)

| Key | Type | Default | Effect | Change when |
|---|---|---|---|---|
| `Strategies` | list | `install_referrer, login, claim_code` | Deterministic strategies in evaluation order; `probabilistic` is **not** in the default | Add `probabilistic` only together with the block below |
| `Probabilistic:Enabled` | bool | false | The opt-in module | Only with a documented legal basis ([compliance/privacy.md](../compliance/privacy.md)) |
| `Probabilistic:WindowMinutes` | int | 60 | Match window — 60, not 7 days ([§A.2.5](../zadanie.md#a25-presnosť-probabilistického-párovania--čísla)) | Do not raise it |
| `Probabilistic:MinConfidence` | double | 0.55 | Below this the result is `none` | Precision vs. recall |
| `Probabilistic:RequireConsent` | bool | true | Refuse signals without `consent.attribution=true` | Never false in the EU |
| `ClaimCode:Enabled` / `TtlMinutes` | bool / int | true / 15 | Redeeming claim codes (iOS SDK `resolve(claimCode:)` → `/v1/resolve`) and issuing them through the API (`POST /v1/claim-codes`, TTL `TtlMinutes`). The edge issues its own on the store interstitial, valid one hour, only in `full` consent mode. Redemption is single-use under concurrency and links only with attribution consent | Longer TTL for slow store installs; the code is ~28 bits, so keep it short |

### `Dle:Privacy` (both hosts — keep identical)

| Key | Type | Default | Effect | Change when |
|---|---|---|---|---|
| `ConsentMode` | enum `off` \| `aggregate_only` \| `full` | `aggregate_only` | Control plane only: the mode given to the bootstrap tenant and to a tenant created without `consent_mode`. Applied at creation only; not a ceiling — a tenant may choose any mode. The edge does not read it ([§E.6.2](../zadanie.md#e62-tri-režimy-prevádzky-produktová-funkcia-nie-prepínač-v-kóde)) | `full` only with a consent flow in place |
| `IpStorage` | enum `none` \| `hash_only` \| `prefix` \| `full` | `hash_only` | Deployment ceiling on what may be derived from an address | `none` for the strictest posture; `prefix` only for the probabilistic module |
| `IpSaltRotationHours` | int | 24 | Not read by either host — only `Crypto:IpSaltRotationHours` takes effect | Never; set the `Crypto` key |
| `Retention:RawDays` | int | 30 | Days raw click events are kept; the control plane's retention job drops older partitions. The edge's `appsettings.json` carries 90, but the edge does not read this key | Policy |
| `Retention:IpPrefixDays` | int | none | Days after which `ip_prefix` is cleared from raw events still inside `RawDays`. Unset, there is no such pass and the prefix goes with its partition | You store prefixes (`IpStorage=prefix`) and want them gone sooner |
| `Retention:AggregatedDays` | int | 730 | Days of aggregates | Policy |
| `Retention:DryRun` | bool | false | The retention job reports what it would drop and changes nothing — it also stops pre-creating partitions, so without pg_partman or a scheduled `dle_*_maintain()` new events land in the default partition | Checking a retention change; never left on |

### `Dle:Analytics`, `Dle:Abuse`, `Dle:Webhooks` (control)

| Key | Type | Default | Effect | Change when |
|---|---|---|---|---|
| `Analytics:Provider` | enum `postgres` \| `clickhouse` | `postgres` | Where rollups and queries run (ADR-006). `clickhouse` is experimental and not wired end to end: the edge still writes clicks to PostgreSQL, the ClickHouse schema script is never applied, and selecting it replaces PostgreSQL retention and partition maintenance with a no-op ([README — Known gaps](../../README.md#known-gaps)) | Not yet — keep `postgres` |
| `Abuse:UrlHausEnabled` | bool | false | Check targets against URLhaus (outbound from control only). Off by default, and no blocklist ships, so no reputation check runs out of the box | On, together with `UrlHausAuthKey`, for any public-facing instance |
| `Abuse:UrlHausAuthKey` | string | empty | abuse.ch Auth-Key sent with every URLhaus lookup. Required when `UrlHausEnabled` is on: the control plane refuses to start without it. A lookup that still fails at runtime counts as no opinion, so the target passes on the remaining checks | Always, when `UrlHausEnabled` is on |
| `Abuse:BlocklistPath` | string | empty | Local blocklist file (hosts / URLs) | Corporate blocklists |
| `Abuse:RecheckIntervalHours` | int | 24 | Nightly re-check of active targets ([§E.3](../zadanie.md#e3-ochrana-proti-zneužitiu-redirektora) step 5) | — |
| `Abuse:ReportsPerHourPerIp` | int | 5 | `POST /abuse-reports` per IP per hour (routed to the control plane by Caddy and the Helm ingress) | — |
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
| `ResolvePermitLimit` / `ResolveWindowMinutes` | int | 5 / 60 | `POST /v1/resolve` per installation (§E.9) |
| `LinkResolveBurstLimit` / `LinkResolveTokensPerPeriod` / `LinkResolvePeriodSeconds` | int | 60 / 30 / 60 | `POST /v1/resolve-link` per installation (direct opens) |
| `LinkResolveAnonymousPerMinute` | int | 30 | `POST /v1/resolve-link` per tenant and caller network when the request carries no `install_id` — keeps a publishable SDK key from becoming a slug-enumeration oracle |

### `Dle:Workers` (control)

Not in `appsettings.json`; the defaults below are the code's (`src/Dle.Control/Workers/WorkerOptions.cs`). Each worker takes `Enabled`, `IntervalMinutes` (0 = the module's own interval) and `RunAtStartup`.

| Key | Type | Default | Effect | Change when |
|---|---|---|---|---|
| `Enabled` | bool | true | Master switch; off registers no worker at all | An API-only control replica |
| `LockTimeoutSeconds` / `StartupJitterSeconds` | int | 10 / 30 | Leader-lock wait per tick; random delay before a worker's first tick | — |
| `DomainVerification:*` | | on, 1440 min, not at startup | Re-verifies every domain's association files; also `FetchTimeoutSeconds` 10, `MaxRedirects` 3, `MaxDocumentBytes` 262144, `MaxDomainsPerRun` 1000, `ReportUnresolvableAsTakeoverRisk` true | Many domains |
| `Rollup:*` | | on, 5 min, at startup | Aggregates clicks into the reporting tables | — |
| `Retention:*` | | on, 360 min, at startup | Drops raw partitions past `Dle:Privacy:Retention:RawDays` and pre-creates the next days' partitions (not with `Retention:DryRun`) | A delicate migration |
| `WebhookDispatch:*` | | on, 0 (follows `Dle:Webhooks:PollIntervalSeconds`), at startup | Delivers the webhook outbox | — |
| `UrlReputation:*` | | on, 1440 min, not at startup | Re-checks targets of active links; also `MaxLinksPerRun` 2000, `Concurrency` 4 | — |
| `GeoIpUpdate:*` | | **off**, 10080 min, not at startup | Downloads the GeoIP database: `DownloadUrl`, `LicenseKey`, `DatabasePath` (the file the edge reads), `TimeoutSeconds` 120, `MaxDownloadBytes` 268435456. Runs in the control plane, so `DatabasePath` must be on a volume the edge also mounts; compose and the Helm chart provide none | You share a GeoIP volume between control and edge |

### `Dle:Telemetry` (control)

| Key | Type | Default | Effect |
|---|---|---|---|
| `ServiceName` | string | `dle-control` | OTel resource |
| `OtlpEndpoint` | string | empty | OTLP target |
| `ConsoleLogging` | bool | true | Structured console logs (what compose/Kubernetes collect) |
| `TraceSampleRatio` | double | 1.0 | Control traffic is low — sample everything |
| `TraceRequests` | bool | true | ASP.NET Core request spans |
| `ExposePrometheusMetrics` | bool | false | Serves `GET /metrics` on the service port (the Helm chart turns it on with its ServiceMonitor) |
| `PrometheusScrapeToken` | string | empty | When set, `/metrics` requires `Authorization: Bearer <token>`; set it whenever an ingress can reach the control plane |

## A production baseline

```bash
# control plane                                              # edge
Dle__Crypto__MasterSecret=<DLE_MASTER_SECRET>                 # Dle__Crypto__MasterSecret=<DLE_EDGE_SECRET>
Dle__Crypto__ClickIdSecret=<DLE_EDGE_SECRET>                  # (the edge needs nothing else from Dle:Crypto)
Dle__Crypto__IpHashSecret=<DLE_EDGE_SECRET>
Dle__Crypto__ClaimCodeSecret=<DLE_EDGE_SECRET>
ConnectionStrings__Postgres=Host=db;Database=dle;Username=dle;Password=…;SSL Mode=Require
ConnectionStrings__Valkey=valkey:6379
Dle__Edge__Network__UseForwardedHeaders=true
Dle__Edge__Network__KnownNetworks__0=10.42.0.0/16          # the proxy's network, nothing wider
Dle__Control__Network__UseForwardedHeaders=true            # the control plane sits behind the same proxy
Dle__Control__Network__KnownNetworks__0=10.42.0.0/16
Dle__Edge__GeoIp__Path=/geoip/GeoLite2-Country.mmdb
Dle__Edge__Interstitial__ProductName=…  Dle__Edge__Interstitial__AppealEmail=abuse@…
Dle__Privacy__ConsentMode=aggregate_only  Dle__Privacy__IpStorage=hash_only  Dle__Privacy__Retention__RawDays=30
Dle__Identity__Oidc__Authority=https://id.example.com/realms/dle  Dle__Identity__Oidc__ClientId=dle-console  Dle__Identity__Oidc__Audience=dle
Dle__Control__InstanceTenantId=<uuid>                       # optional: the bootstrapped tenant is the operator otherwise
Dle__Abuse__UrlHausEnabled=true  Dle__Abuse__UrlHausAuthKey=<your abuse.ch Auth-Key>   # the key is required; control refuses to start without it
Dle__Telemetry__OtlpEndpoint=http://otel-collector:4317
```

Feature flags for the risky modules (probabilistic matching, ClickHouse sink, PQC signing) are ordinary options and take effect on restart; none requires a redeploy of a different image ([§C.4](../zadanie.md#c4-konfigurácia)). The ClickHouse sink is not wired end to end — leave it off (`Analytics:Provider` above).
