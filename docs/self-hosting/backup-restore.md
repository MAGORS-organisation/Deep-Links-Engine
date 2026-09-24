# Backup and restore

**What this is:** what must be backed up, how often, and how to prove a restore works. **Who it is
for:** the operator responsible for the instance's continuity.

## Targets

The specification sets **RPO ≤ 5 minutes** for the control plane and **RTO ≤ 30 minutes**
([NFR-07](../zadanie.md)). Those are the numbers a DORA-regulated customer will ask you to evidence
([compliance/regulatory-map.md](../compliance/regulatory-map.md)).

Profile A (compose) does not meet the RPO as shipped: its backup is a copy of the `postgres_data`
volume, and nothing configures WAL archiving ([deploy/README — Production changes](../../deploy/README.md#production-changes-profile-a),
[README — Known gaps](../../README.md#known-gaps)). A 5-minute RPO needs the WAL archiving described
below, set up by you, or a managed PostgreSQL that provides it.

## What to back up

| Asset | Where | Loss means | Method |
|---|---|---|---|
| **PostgreSQL** — tenants, domains, apps, links, keys, installs, attributions, audit log, webhook queue, click-event partitions | the `postgres` service / your managed cluster | Everything | Continuous WAL archiving plus a daily base backup (`pg_basebackup`), or your operator's equivalent. This is what meets a 5-minute RPO; a nightly `pg_dump` or a volume copy does not. Profile A ships neither — see above |
| **`Dle:Crypto:MasterSecret`** | your secret store (`.env`, Kubernetes Secret, vault) | Stored slugs keep resolving, but the slug permutation changes, so new slugs may collide with existing ones. Click ids already issued stop decoding. IP hashes no longer match earlier ones. The webhook signing key changes, and stored webhook secrets can no longer be decrypted, so deliveries fail until the subscriptions are recreated | Store it in the secret manager you use for everything else, and in an offline copy. It is small; losing it is the single worst outcome |
| **Signing keys** | The webhook Ed25519 key is derived from the master secret; any others are configured under `Dle:Crypto:Keys`. The `signing_keys` table exists but nothing reads or writes it | Webhook receivers cannot verify deliveries; JWKS changes | Covered by backing up the master secret and the configuration |
| **GeoIP database** (`GeoLite2-City.mmdb`) | edge volume | Country-based routing rules fall through to the default rule; `dle_geoip_available` drops to 0 (no alert ships — see [operations/runbook.md](../operations/runbook.md)) | Re-downloadable from MaxMind; do not treat it as data |
| **Valkey** | `valkey` service | Nothing. It is a cache and refills from PostgreSQL | Do not back up |
| **Uploaded assets** (interstitial branding images, QR logos, if configured) | control volume or object store | Cosmetic | Include in the file-level backup |

Click events are the largest table and the least valuable per byte; the default retention is 30 days
raw and 730 days aggregated ([FR-247](../zadanie.md)). If backup size is a concern, exclude detached
partitions older than the retention window — they are dropped by the retention worker anyway.

## Restore procedure

1. Provision PostgreSQL at the same major version and restore the base backup plus WAL up to the
   chosen point in time (on an unmodified Profile A: the `postgres_data` volume copy, which restores
   to the moment it was taken).
2. Provision the secret store with the **same** `Dle:Crypto:MasterSecret` and any configured signing
   keys. Verify before starting services: a wrong master secret will not error, and links restored
   from the backup still resolve with it — it silently changes new slugs, click ids and every other
   derived key. The `kid` check in step 4 catches it: the webhook key and its `kid` are derived from
   the master secret.
3. Start the stack ([quickstart.md](quickstart.md)). The migration step is a no-op when the schema
   is current.
4. Verify:
   - `GET /.well-known/jwks.json` lists the expected `kid` values.
   - Resolve three known links from before the incident; each returns its expected target.
   - The admin console shows the expected tenants, domains and link counts.
   - A webhook test delivery verifies against the receiver's stored secret.
5. Re-download the GeoIP database if the edge volume was lost.
6. Record the drill: start time, restore point, end time, discrepancies.

## Drill

Run the restore into a scratch environment **quarterly**, from production backups, and time it. If the
wall-clock time from "decision to restore" to step 4 passing exceeds 30 minutes, the RTO is not real.
Keep the drill record; it is the artefact an auditor asks for.

## What a backup does not protect against

- A quarantined link stays quarantined after a restore, by design; quarantine is a record, not a state to
  roll back ([compliance/security-overview.md](../compliance/security-overview.md)).
- Apple's and Google's cached association files are outside your control. A restored domain with an
  unchanged app configuration needs no propagation wait; a changed one waits up to seven days
  ([domains.md](domains.md)).
