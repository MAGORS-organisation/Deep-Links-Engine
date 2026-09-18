# Deploying Deep Link Engine

Two supported shapes, both from `docs/zadanie.md` §B.8:

| Profile | Where | What | Files |
|---|---|---|---|
| **A — one server** | Docker Compose | Caddy (TLS, HTTP/2+3) → 2× dle-edge + 1× dle-control → postgres:18 + valkey:8. Sized for 2 vCPU / 4 GB / 20 GB, ~1 000 req/s (NFR-09, NFR-10). | `docker-compose.yml`, `docker-compose.override.yml`, `.env.example`, `Caddyfile`, `docker/`, `postgres/` |
| **B — production cluster** | Kubernetes + Helm | Ingress → dle-edge (HPA 3–20) + dle-control (2) → external PostgreSQL primary + read replicas, Valkey cluster, optional ClickHouse. | `helm/dle/` |

Both run the same two images (`ghcr.io/magors-organisation/dle-edge`, `…/dle-control`, built by `docker/Dockerfile.*`) with the same environment variable names (`Dle__Section__Key` ↔ `Dle:Section:Key`; the full inventory is `src/Dle.Edge/appsettings.json` and `src/Dle.Control/appsettings.json`). `deploy/aspire/` is the developer inner loop and is not covered here.

> **The one rule that breaks apps silently:** `https://<host>/.well-known/apple-app-site-association` and `/.well-known/assetlinks.json` must answer **200, never any 3xx** — no HTTP→HTTPS redirect, no trailing slash, no `www` canonicalisation (§A.2.1). Both profiles are built around that; see [Never redirect /.well-known](#never-redirect-well-known) before you put anything in front of them.

---

## Profile A — one server, five commands

Prerequisites: Docker Engine 24+ with the compose plugin, a DNS name pointing at the host, ports 80 and 443 (TCP and UDP) open.

```bash
cd deploy
cp .env.example .env                                   # 1. edit: DLE_DOMAIN, DLE_TLS, POSTGRES_PASSWORD, DLE_MASTER_SECRET
docker compose -f docker-compose.yml pull              # 2. or `build` to build from ../src
docker compose -f docker-compose.yml up -d             # 3. postgres+valkey → migrate → edge+control → caddy
docker compose -f docker-compose.yml ps                # 4. everything `healthy`, migrate `exited (0)`
curl -sI "https://$(grep ^DLE_DOMAIN .env | cut -d= -f2)/.well-known/apple-app-site-association" | head -1   # 5. → 200
```

Required in `.env` (compose refuses to start without them): `DLE_DOMAIN` (the public link host), `POSTGRES_PASSWORD`, and `DLE_MASTER_SECRET` — at least 32 characters, `openssl rand -base64 48`. `DLE_TLS` is an e-mail address for ACME certificates or `internal` for Caddy's local CA. Every other variable has a default and is documented in `.env.example`.

Naming the file (`-f docker-compose.yml`) keeps `docker-compose.override.yml` out of the stack. That override is for **local development only**: `docker compose up --build` in this directory publishes edge (8080), control (8081), postgres and valkey on 127.0.0.1, runs one edge replica, turns on tenant self-service and `PublicScheme=http`, and keeps Caddy on `https://localhost` with its own CA.

Then: admin console `https://<host>/admin/`, API reference `https://<host>/scalar`, links `https://<host>/<slug>`.

### Production changes (Profile A)

1. **Secrets.** `POSTGRES_PASSWORD` is only read while postgres initialises an empty volume — set it before the first `up`. `DLE_MASTER_SECRET` derives every keyed primitive (slug permutation, IP hashing salt, signing-key wrapping): back it up together with the database; changing it changes every slug.
2. **Pin images.** `DLE_VERSION=<release>` instead of `latest` (or `DLE_EDGE_IMAGE` / `DLE_CONTROL_IMAGE` with digests).
3. **pg_partman.** The official `postgres:18` image has no `pg_partman`. Build `docker build -t dle-postgres:18 -f docker/Dockerfile.postgres docker` and set `POSTGRES_IMAGE=dle-postgres:18` **before the first start** (extensions are created by `postgres/init/*.sql` on an empty data directory only). See [pg_partman](#pg_partman-and-click_events-retention).
4. **Trust boundary.** The edge honours `X-Forwarded-*` only from `DLE_NETWORK_SUBNET` (the compose network, where Caddy lives). If you put a CDN/WAF in front of Caddy, raise `Dle__Edge__Network__ForwardLimit` in `docker-compose.yml` by one per appending layer — never widen `KnownNetworks` to the internet: the client address is the rate-limit key, the click identifier and the GeoIP key.
5. **GeoIP.** Put the MaxMind mmdb in `DLE_GEOIP_DIR` (mounted read-only at `/geoip`) and set `DLE_GEOIP_PATH=/geoip/GeoLite2-Country.mmdb`. The edge never downloads it (NFR-14).
6. **Identity.** Set `DLE_INSTANCE_TENANT_ID` (multi-tenant) or `DLE_ALLOW_TENANT_SELF_SERVICE=true` (single organisation) — with neither, the tenant routes deny everyone, deliberately — and the OIDC triple `DLE_OIDC_AUTHORITY / CLIENT_ID / AUDIENCE` for the admin console. See [First credential](#first-credential).
7. **Backups.** Volume `postgres_data` (PGDATA under `/var/lib/postgresql`) plus the master secret; `caddy_data` holds certificates and the ACME account (losing it costs Let's Encrypt rate-limit budget). Valkey is cache only and needs no backup.
8. **Upgrades.** `docker compose -f docker-compose.yml pull && docker compose -f docker-compose.yml up -d` — the `migrate` service runs the EF Core bundle (idempotent) before edge and control restart.

### First credential

There is **no seeded API key** in the control plane, and there is no anonymous route that creates one: every request to `/api/v1/*` must carry an API key or an OIDC bearer token, tenants are created with `POST /api/v1/tenants` (FR-241) by an *instance operator* — an **authenticated** caller for whom either `Dle:Control:AllowTenantSelfService` is on or whose tenant is `Dle:Control:InstanceTenantId` — and API keys with `POST /api/v1/api-keys` (FR-242) inside a tenant.

The first credential is therefore a **human signed in through OIDC**:

1. Configure `Dle:Identity:Oidc` (`DLE_OIDC_AUTHORITY`, `DLE_OIDC_CLIENT_ID`, `DLE_OIDC_AUDIENCE`; in Helm `control.config.oidc.*`). Tokens must carry the tenant and role claims the control plane reads (`dle_tenant`, `dle_role` by default — `Dle:Identity:Oidc:TenantClaim` / `RoleClaim` in `src/Dle.Control/appsettings.json`).
2. For a single-organisation deployment set `Dle:Control:AllowTenantSelfService=true`; sign in to `https://<host>/admin/` and create the tenant, then an API key for automation. Turn self-service off again afterwards if you do not want every signed-in user to be able to create tenants.
3. For a multi-tenant deployment set `Dle:Control:InstanceTenantId` to the operating tenant's id; its owners then create the other tenants.

`Dle:Identity:Oidc:RequireHttpsMetadata` stays `true` in production (the compose override switches it off for `localhost` only).

---

## Profile B — Kubernetes with Helm, five commands

Prerequisites: Kubernetes ≥ 1.29, Helm ≥ 3.12, an ingress controller (the chart renders the redirect exemption for **ingress-nginx** or **Traefik**, `ingress.controller`), a PostgreSQL 16+ (18 recommended) reachable from the cluster with `citext` (and ideally `pg_partman`), and a Valkey/Redis-compatible cache. Optional: cert-manager, Prometheus Operator, a metrics adapter for request-rate autoscaling.

```bash
kubectl create namespace dle
kubectl -n dle create secret generic dle-secrets \
  --from-literal=master-secret="$(openssl rand -base64 48)" \
  --from-literal=postgres-connection-string='Host=pg-primary.db;Port=5432;Database=dle;Username=dle;Password=…;SSL Mode=Require' \
  --from-literal=postgres-read-connection-string='Host=pg-ro.db;Port=5432;Database=dle;Username=dle;Password=…;SSL Mode=Require' \
  --from-literal=valkey-connection-string='valkey-0.cache:6379,valkey-1.cache:6379,valkey-2.cache:6379'
helm dependency build deploy/helm/dle                  # Helm requires the (disabled) subcharts to be present; or pass --dependency-update below
helm upgrade --install dle deploy/helm/dle -n dle -f deploy/helm/dle/values-production.yaml \
  --set ingress.host=go.example.com --set secrets.existingSecret=dle-secrets
kubectl -n dle rollout status deploy/dle-edge && curl -sI https://go.example.com/.well-known/apple-app-site-association | head -1   # → 200
```

What the release contains (`helm/dle/templates/`):

| Object | Notes |
|---|---|
| `dle-edge` Deployment, Service, **HPA 3–20**, PDB (min 2) | Liveness *and* readiness on `/healthz` — the edge must keep serving from cache while PostgreSQL is down (§D.6), so readiness must not gate on the database. Spread across zones and nodes. |
| `dle-control` Deployment (2), Service, PDB (min 1) | Readiness `/readyz` (gates on the database), liveness `/healthz`. |
| `dle-migrate` Job | `helm.sh/hook: pre-install,pre-upgrade` — runs `/app/efbundle` (EF Core migration bundle) from the control image before the new pods roll. Expand/contract-safe migrations only: the previous version still serves during the hook. |
| Ingress ×2 (`dle`, `dle-well-known`), +1 for `ingress.controlHost` | See [Never redirect /.well-known](#never-redirect-well-known). |
| NetworkPolicy ×3 | Edge: ingress only from the ingress controller; **egress only to kube-dns, PostgreSQL, Valkey** (NFR-14). Control: ingress only from the controller. Migrate: DNS + PostgreSQL. |
| ConfigMap ×2, Secret (only without `existingSecret`), ServiceAccount (token not mounted) | |
| ServiceMonitor | Off by default: the images export OTLP, not `/metrics`. |

Every pod runs as uid 1654 with `readOnlyRootFilesystem`, `allowPrivilegeEscalation: false`, all capabilities dropped and `seccompProfile: RuntimeDefault`; `/tmp` is an emptyDir. `values.schema.json` refuses values that weaken that, and refuses redirect/rewrite annotations on the well-known Ingress.

### Production values (Profile B)

`helm/dle/values-production.yaml` is the starting point; every `CHANGE ME` in it needs a decision:

- **Images** — pin `image.edge.tag` / `image.control.tag`. Prefer the `runtime-chiseled` targets of the Dockerfiles (no shell, no package manager) when your pipeline publishes them; the chart never executes a binary inside the images.
- **Secrets** — `secrets.existingSecret` with the keys above (External Secrets Operator, sealed-secrets, SOPS). Inline `secrets.masterSecret` works for evaluation but lands in Helm's release history, and the migration hook then needs a hook-scoped copy that `helm uninstall` does not remove (`kubectl -n dle delete secret dle-migrate`). The hook's ServiceAccount `dle-migrate` is left behind for the same reason (`kubectl -n dle delete serviceaccount dle-migrate`); it exists because a `pre-install` hook cannot name an account the release has not created yet, and it carries `serviceAccount.annotations`, so an IRSA or Workload Identity binding reaches the migration as well as the pods.
- **Data stores** — external. The bundled Bitnami `postgresql` / `valkey` subcharts (`postgresql.enabled`, `valkey.enabled`) are evaluation conveniences; with them the migration hook moves to `post-install` and the Bitnami image lacks `pg_partman`.
- **Ingress** — `ingress.className`, `ingress.controller` (`nginx` | `traefik` | `none`), `ingress.host`, TLS via `ingress.tls.certManager.clusterIssuer` or your own `ingress.tls.secretName`. `exposeControlOnHost: false` + `controlHost` keeps the admin console and API off the link domain (`/.well-known/jwks.json` stays on the link host).
- **Trust boundary** — `edge.config.network.knownNetworks` narrowed to the ingress controller's CIDR; `forwardLimit` = 1 + the number of appending layers in front (CDN/WAF, cloud L7 balancer).
- **Network policy** — name PostgreSQL and Valkey (`networkPolicy.postgres.cidrs` or selectors; same for `valkey`) — otherwise the edge may reach *any* destination on 5432/6379 and NOTES.txt warns. An OTLP collector needs an `networkPolicy.edge.extraEgress` entry and must live on your network.
- **Autoscaling** — CPU at 60–65 % of `edge.resources.requests.cpu`; enable `edge.autoscaling.rps` once a metrics adapter serves the per-pod request rate (~400 req/s per 0.75-CPU pod). The behaviour block scales up without a stabilisation window (§D.6: 10× traffic, p99 ≤ 200 ms while scaling).
- **Connections** — `edge.config.dbPool.maxPoolSize × maxReplicas + control` must fit `max_connections` (or put PgBouncer in front).
- **GeoIP** — a PVC you fill yourself (`edge.config.geoIp.existingClaim` + `path`).
- **Identity** — `control.config.instanceTenantId` or `allowTenantSelfService`, and `control.config.oidc.*` — see [First credential](#first-credential).

Upgrades: `helm upgrade dle deploy/helm/dle -n dle -f … ` runs the migration hook, then rolls control (`maxSurge 1, maxUnavailable 0`) and edge (`maxSurge 25 %, maxUnavailable 0`) with a 5 s pre-stop pause so the controller drains endpoints without 502s. Rollback: `helm rollback dle` (migrations are not rolled back — test them on a copy of production first, §D.7).

---

## Resources (NFR-10)

**Profile A** fits 2 vCPU / 4 GB RAM / 20 GB disk. CPU limits are ceilings and deliberately sum to more than 2 vCPU so idle capacity can be borrowed; memory limits leave ~0.8 GB for the kernel and page cache.

| Service | CPU limit | Memory limit | `.env` |
|---|---|---|---|
| dle-edge ×2 | 0.75 each | 384 M each | `DLE_EDGE_CPUS`, `DLE_EDGE_MEMORY`, `DLE_EDGE_REPLICAS` |
| dle-control | 0.5 | 512 M | `DLE_CONTROL_CPUS`, `DLE_CONTROL_MEMORY` |
| postgres | 1.0 | 1536 M | `DLE_POSTGRES_CPUS`, `DLE_POSTGRES_MEMORY` (tuning in `postgres/postgresql.tuning.conf`) |
| valkey | 0.25 | 320 M (`maxmemory` 256 mb, LRU) | `DLE_VALKEY_CPUS`, `DLE_VALKEY_MEMORY`, `VALKEY_MAXMEMORY` |
| caddy | 0.5 | 128 M | `DLE_CADDY_CPUS`, `DLE_CADDY_MEMORY` |
| migrate (one-shot) | 0.5 | 256 M | — |
| **Total memory** | | **≈ 3.2 GB** | |

Disk: PostgreSQL data (daily `click_events` partitions, 180-day retention) dominates; container logs are capped (`json-file`, 5 × 20 MB per service); images ≈ 1 GB. Throughput at that size is ~1 000 req/s across two edge replicas (~500 req/s per 0.75-CPU replica).

**Profile B** per pod (`values.yaml`): edge requests 250 m / 192 Mi, limits 1 CPU / 384 Mi, 3–20 pods; control requests 200 m / 256 Mi, limits 1 CPU / 512 Mi, 2 pods; migrate 100 m / 128 Mi. `values-production.yaml` raises the requests (500 m / 256 Mi edge, 250 m / 384 Mi control) so the HPA's percentage has a meaningful base. Data stores are external and sized by their operators; §B.8 wants a PostgreSQL primary with two read replicas and a three-shard Valkey cluster.

---

## pg_partman and click_events retention

`click_events` is partitioned by day with 180-day retention (§B.5.2). Partition maintenance has two implementations, chosen automatically by the `InitialSchema` migration at migration time:

- **`pg_partman` present** (`deploy/docker/Dockerfile.postgres` = `postgres:18` + the PGDG `postgresql-18-partman` package; RDS/Aurora, Azure Flexible Server, Cloud SQL and CrunchyData images also ship it): the migration registers the parent with pg_partman, which pre-creates and drops partitions on its own (with `pg_partman`'s background worker or its `run_maintenance()` on a schedule).
- **Absent** (official `postgres:18`, the Bitnami subchart): the migration raises a WARNING and installs the plain-SQL fallbacks **`dle_click_events_maintain()`** and **`dle_sdk_events_maintain()`** (both thin wrappers over `dle_partitions_maintain(table, days_ahead, retention_days)`), which pre-create 7 days of partitions and drop those past retention. They must run **daily**: `pg_cron` (`SELECT cron.schedule('dle-partitions', '5 0 * * *', 'SELECT dle_click_events_maintain(), dle_sdk_events_maintain()')`), a host cron running `psql -c 'SELECT dle_click_events_maintain(), dle_sdk_events_maintain();'`, or a Kubernetes CronJob with the same statement against the primary. The control plane's retention job also calls both on every run (it keeps seven days of partitions ready and adopts any day whose rows landed in the default partition), so a running control plane is itself a sufficient schedule.

Switching later: install the extension and run the partitioning script again so it hands the parent table over to pg_partman (its WARNING says so). An already-applied EF migration is skipped by the bundle, so on an existing database that is an operator step — the script is `ConfigurePartitioning` in `src/Dle.Persistence/Sql/DlePostgresScripts.cs`. `postgres/init/01-extensions.sql` installs `citext` and, when available, `pg_partman` into schema `partman` on the first initialisation only.

---

## Never redirect /.well-known

Apple's CDN fetches `apple-app-site-association` and Google's verifier fetches `assetlinks.json`; **both refuse any 3xx** — `301/308` HTTP→HTTPS, a trailing-slash canonicalisation, a `www` redirect, a "pretty URL" rewrite. The failure is silent: Universal Links and App Links simply stop opening the app, and a fix takes up to 7 days to propagate (§A.2.1). The rest of the site may redirect freely.

How each profile enforces it:

- **Profile A (Caddy)** — `auto_https disable_redirects` switches off Caddy's global HTTP→HTTPS redirect; the `http://` site block serves `/.well-known/*` straight through and redirects everything else with a 308. Never add a `redir` or `uri` rewrite in front of the well-known handlers.
- **Profile B (Ingress)** — the chart renders a **separate Ingress `<release>-well-known`** for the two association paths so that only it carries the controller's exemption: ingress-nginx `ssl-redirect=false` + `force-ssl-redirect=false` (applied per location, so the main Ingress may still redirect); Traefik gets two Ingresses (entrypoints `web` and `websecure`) because a Traefik router's TLS setting covers all of its entrypoints, and the HTTP one carries a priority above Traefik's entrypoint-level redirection router. `ingress.controller: none` renders no dialect — supply the equivalent in `ingress.wellKnownAnnotations`, which the values schema refuses to accept redirect/rewrite annotations for.
- **In front of both** — a CDN/WAF that "forces HTTPS" must exempt `/.well-known/*`; `/.well-known/jwks.json` (public signing keys) goes to the control plane and may be cached, the association files are generated per host from tenant configuration and should not be cached longer than a few minutes.

Verify after **every** change to Caddy, the Ingress, the CDN or DNS — both lines must print a 200:

```bash
curl -sI http://go.example.com/.well-known/apple-app-site-association  | head -1
curl -sI https://go.example.com/.well-known/apple-app-site-association | head -1
```

`.github/scripts/verify-domains.py` is the nightly complement: it asks the control plane (`POST /api/v1/domains/{id}/verify`) to re-verify every registered domain's association files (§D.7 item 7).
