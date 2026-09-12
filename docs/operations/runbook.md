# On-call runbook

**What this is:** the alerts this system raises, what each one means, and the first three things to do.
**Who it is for:** whoever is on call for the instance. Metric names are from
[architecture/observability.md](../architecture/observability.md); the degradation rules from
[docs/zadanie.md §D.6](../zadanie.md).

## Principles

- **The resolve path is sacred.** Users must reach their destination even when analytics, the cache or
  the database are degraded. If a fix risks the resolve path, do the fix later.
- **Fail-open on telemetry, fail-closed on safety.** Dropped click events are a metric, not an outage.
  A target that cannot be validated is not redirected.
- **Never answer 500 to a public endpoint.** The edge answers 503 when it genuinely cannot serve; if you
  see 500s, that is a bug to file, not a tuning problem.

## Alerts

### `dle_click_events_dropped_total` increasing

**Meaning.** The bounded channel between the resolver and the batch writer is full; clicks are being
served but not recorded. Usually the writer cannot keep up with PostgreSQL, or PostgreSQL is slow.
1. Check PostgreSQL latency and connections (`pg_stat_activity`); check disk space — a full disk shows
   up here first.
2. Check the writer's logs for COPY failures; a schema mismatch after an upgrade fails every batch.
3. If sustained, scale the control plane or raise the channel capacity; the user experience is
   unaffected either way, but the campaign numbers are under-counted for the duration.

### `dle_domain_verification_failures` > 0

**Meaning.** The nightly verifier found a domain whose `apple-app-site-association` or `assetlinks.json`
is unreachable, redirected, wrong content type, or does not list the registered app. Links on that
domain will stop opening the app for new installs within days.
1. Open the domain in the console; the failure reason is specific (`well_known.redirect`,
   `well_known.content_type`, `well_known.appid_mismatch`, …).
2. `curl -sI https://<host>/.well-known/apple-app-site-association` — must be `200`, `application/json`,
   **no `Location` header**. A proxy or CDN in front of the engine adding an HTTPS or trailing-slash
   redirect is the usual cause ([self-hosting/domains.md](../self-hosting/domains.md)).
3. If the app configuration changed, remember propagation takes up to seven days on both platforms; the
   alert is telling you about the file, not about devices.

### Resolve latency: `dle_resolve_duration_seconds` p99 > 50 ms

**Meaning.** The hot path is slower than the budget ([NFR-01](../zadanie.md)).
1. Check `dle_cache_hit_ratio` for L1 and L2. A cold Valkey after a restart or a cache-invalidating
   bulk import is the usual cause and self-heals.
2. If L2 is down (`cache_l2_down`), the edge is serving from L1 plus PostgreSQL — expected to be slower
   but functional. Restore Valkey; nothing needs to be re-warmed.
3. If hit ratio is fine and latency is not, look at GeoIP (a corrupt file makes lookups slow before they
   fail) and at the reverse-DNS bot verifier's cache.

### Edge answering 503 on `/{slug}`

**Meaning.** PostgreSQL is unreachable and the link is not in cache. Cached links continue to resolve.
1. Confirm PostgreSQL is the cause (`/readyz` on control is also failing).
2. Restore the database; do **not** restart the edge — its L1 cache is what is keeping known links alive.
3. After recovery, verify a previously uncached link resolves and `/readyz` is green.

### `dle_webhook_dlq_size` growing

**Meaning.** Deliveries to a customer endpoint have exhausted their retries.
1. Open the webhook's deliveries in the console; the response code and body of the last attempt are
   recorded.
2. Common causes: the receiver rejects the signature after rotating its secret without updating the
   engine, or the endpoint moved. Use the test-delivery endpoint to confirm.
3. Once fixed, re-drive the dead letters; they carry the original payload and a new timestamp.

### Abuse report received (critical reason)

**Meaning.** Someone reported a link as phishing or malware. The specification's SLA is a reaction within
four hours ([§E.3](../zadanie.md)).
1. Open the triage queue; the report has the link id, reason and reporter hash.
2. If plausible, **quarantine** the link. It answers 410 with an explanation immediately; nothing is
   deleted, so the record survives for the notice-and-action trail.
3. Record the decision with a note. That note is what a later complaint is answered from.

### GeoIP database missing or stale

**Meaning.** Country is `null` on every click; geo rules fall through to their default.
1. Check the edge volume for `GeoLite2-City.mmdb` and the update worker's last run.
2. Re-download and restart the update worker; the edge reloads without a restart.
3. Campaigns with country rules ran with the default rule during the gap — tell the marketer.

## Escalation

Security issues: [SECURITY.md](../../SECURITY.md). Anything that looks like cross-tenant data exposure
is a security incident first and an operations issue second.
