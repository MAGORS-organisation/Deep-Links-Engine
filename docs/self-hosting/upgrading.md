# Upgrading

**What this is:** how to move a running instance to a newer release without losing links, keys or
attribution history. **Who it is for:** the operator of a self-hosted instance.

## Before you start

| Check | Why |
|---|---|
| Read the release notes and [CHANGELOG.md](../../CHANGELOG.md) for the versions you are crossing | Migrations and configuration keys are listed there per release |
| Take a backup ([backup-restore.md](backup-restore.md)) and confirm you can restore it | An upgrade that touches the schema is the moment a backup earns its keep |
| Confirm the PostgreSQL major version | Minimum **16**, recommended **18**. On 16 and 17 the schema installs a `dle_uuidv7()` shim; on 18 it uses the native `uuidv7()` ([ADR-0003](../adr/0003-postgresql-as-primary-store.md)) |
| Check `Dle:Crypto:MasterSecret` is in your secret store, not only in the running container | It keys the slug permutation. Losing it changes every public URL ([ADR-0007](../adr/0007-slug-generation-keyed-feistel-base62.md)) |

## How the schema is migrated

Every release ships its EF Core migrations inside the control image as `/app/efbundle`, a
framework-dependent `dotnet ef migrations bundle`. Both deployment profiles run it **before** the new
control plane starts:

- **Compose (Profile A):** the `migrate` service in [`deploy/docker-compose.yml`](../../deploy/docker-compose.yml)
  runs the bundle once and exits; `dle-control` depends on it completing.
- **Helm (Profile B):** `templates/migrate-job.yaml` is a `pre-install,pre-upgrade` hook Job.

The edge resolver does not run migrations and keeps serving from cache while they run — that is the
degradation path the specification requires ([§D.6](../zadanie.md)).

## Procedure — compose

```bash
cd deploy
docker compose pull                 # fetch the new images
docker compose up -d migrate        # apply the schema; exits 0 when done
docker compose up -d                # roll the services; edge replicas restart one at a time
docker compose ps                   # every service healthy
curl -sI https://<domain>/.well-known/apple-app-site-association | head -1   # must be 200, no redirect
```

## Procedure — Helm

```bash
helm repo update   # or: helm pull oci://ghcr.io/magors-organisation/charts/dle --version <new>
helm upgrade dle oci://ghcr.io/magors-organisation/charts/dle --version <new> -f values-production.yaml
kubectl rollout status deploy/dle-edge
kubectl rollout status deploy/dle-control
```

The pre-upgrade hook fails the release if the migration fails, so the old pods keep running.

## Rolling back

| Layer | How | Caveat |
|---|---|---|
| Application images | `docker compose` with the previous tag, or `helm rollback dle <revision>` | Safe on its own only if the schema is compatible with the older code |
| Schema, additive migration | Leave it; older code ignores columns it does not know | This is the normal case — releases aim for additive migrations |
| Schema, breaking migration | Restore the pre-upgrade backup | Release notes flag these explicitly. `dotnet ef database update <previous>` exists but partition and `pg_partman` changes are not reversible by EF, so the backup is the honest path |

## Things that are not affected by an upgrade, and things that are

- Association files (`apple-app-site-association`, `assetlinks.json`) are regenerated from the database
  on request and cached for 15 minutes. An upgrade does not change them unless the release notes say the
  generator changed — in which case remember that Apple and Android 15+ take up to seven days to pick
  the change up ([domains.md](domains.md)).
- Signing keys stay valid across upgrades; rotation is a separate operator action, and the verifier
  accepts every key still inside its validity window ([ADR-0013](../adr/0013-crypto-agility-from-day-one.md)).
- SDK compatibility: the wire contract is additive. A new server field is ignored by an older SDK; a
  removed or renamed field is a breaking change and the contract test suite refuses it in CI. Upgrade
  the server first, then the SDKs at your app's own release cadence.
- Valkey holds cache only; it can be emptied at any time and refills from PostgreSQL.

## Verifying an upgrade

1. `GET /healthz` on both hosts returns 200; `GET /readyz` on control returns 200.
2. A known link resolves with the expected 302 / interstitial.
3. `GET /.well-known/jwks.json` still lists the current signing key.
4. The admin console's domain page shows every domain still verified.
5. Check `dle_click_events_dropped_total` did not increase during the rollout ([operations/runbook.md](../operations/runbook.md)).
