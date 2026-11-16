# Changelog

All notable changes to Deep Link Engine are documented here. The format follows
[Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). Until `1.0.0`, minor versions may
contain breaking changes; they are called out as such.

Entries under *Unreleased* are written by the pull request that makes the change.

## Unreleased

First public release. The project was developed privately before this; its public history starts
here.

### Added

- **Edge (`dle-edge`)**: link resolution on branded domains with generated or custom slugs, routing
  rules (platform, OS and app version, country, language, in-app browser, time window, A/B split),
  Open Graph previews for verified crawlers, QR codes, an interstitial for in-app browsers, and
  generated `apple-app-site-association` / `assetlinks.json`. Never a `301`.
- **Control plane (`dle-control`)**: HTTP API with an OpenAPI description for links, domains (with
  ownership verification), apps, API keys and tenants; OIDC sign-in for the admin console; SDK
  endpoints (`/v1/resolve`, `/v1/resolve-link`, `/v1/claim-codes`, `/v1/login-hints`, `/v1/events`);
  analytics; signed webhooks (HMAC-SHA256 + Ed25519) with delivery replay; DSA Art. 16 abuse
  reports with quarantine; an audit log; a tenant data export and erasure by `install_id`.
- **Attribution** that records a `match_type` and a confidence for every result: Play Install
  Referrer, iOS claim code, login reconciliation, direct open, and opt-in probabilistic matching,
  under three consent modes per tenant.
- **Admin console** and **SDKs** for Android, iOS and the web.
- **Deployment**: a Docker Compose stack behind Caddy and a Helm chart with an edge autoscaler and a
  NetworkPolicy, on PostgreSQL 18 and Valkey 8; telemetry over OTLP and an opt-in `GET /metrics`
  with an optional bearer token (required by the Helm chart's ServiceMonitor).
- **Supply chain**: locked restores, GitHub Actions pinned to commit SHAs, and a release workflow
  that signs keylessly with Sigstore and ships SBOM, CBOM and SLSA provenance.

### Known limitations

Nothing has been run on real phones, under load, or through an external penetration test, and no
image, chart or SDK has been published to a registry yet. The README lists what does not work end
to end under [Known gaps](README.md#known-gaps).
