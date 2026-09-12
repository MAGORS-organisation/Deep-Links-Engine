# Backup and restore

**What this is:** what must be backed up, how often, and how to prove a restore works. **Who it is
for:** the operator responsible for the instance's continuity.

## Targets

The specification sets **RPO ≤ 5 minutes** for the control plane and **RTO ≤ 30 minutes**
([NFR-07](../zadanie.md)). Those are the numbers a DORA-regulated customer will ask you to evidence
([compliance/regulatory-map.md](../compliance/regulatory-map.md)).

## What to back up

| Asset | Where | Loss means | Method |
|---|---|---|---|
| **PostgreSQL** — tenants, domains, apps, links, keys, installs, attributions, audit log, webhook queue, click-event partitions | the `postgres` service / your managed cluster | Everything | Continuous WAL archiving plus a daily base backup (`pg_basebackup`), or your operator's equivalent. This is what meets a 5-minute RPO; a nightly `pg_dump` does not |
| **`Dle:Crypto:MasterSecret`** | your secret store (`.env`, Kubernetes Secret, vault) | Every public short URL changes: the slug permutation is keyed by it. Click ids stop decoding. Existing links become unreachable | Store it in the secret manager you use for everything else, and in an offline copy. It is small; losing it is the single worst outcome |
| **Signing keys** (`signing_keys` table, plus any configured keys) | PostgreSQL and configuration | Webhook receivers cannot verify deliveries; JWKS changes | Covered by the database backup; configured keys are covered by the secret store |
| **GeoIP database** (`GeoLite2-City.mmdb`) | edge volume | Country-based routing rules fall through to the default rule; an alert fires | Re-downloadable from MaxMind; do not treat it as data |
| **Valkey** | `valkey` service | Nothing. It is a cache and refills from PostgreSQL | Do not back up |
| **Uploaded assets** (interstitial branding images, QR logos, if configured) | control volume or object store | Cosmetic | Include in the file-level backup |

Click events are the largest table and the least valuable per byte; the default retention is 30 days
raw and 730 days aggregated ([FR-247](../zadanie.md)). If backup size is a concern, exclude detached
partitions older than the retention window — they are dropped by the retention worker anyway.

## Restore procedure

1. Provision PostgreSQL at the same major version and restore the base backup plus WAL up to the
   chosen point in time.
2. Provision the secret store with the **same** `Dle:Crypto:MasterSecret` and any configured signing
   keys. Verify before starting services: a wrong master secret will not error — it will silently
   produce different slugs.
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
