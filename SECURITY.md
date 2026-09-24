# Security policy

Deep Link Engine (DLE) redirects people. A bug in it can send a person somewhere they did not
intend to go, or tell a third party something about them that they did not consent to share.
Reports are taken seriously and handled in the open once they are fixed.

This document is the S-11 deliverable of `docs/zadanie.md` §E.8: a published contact and a
90-day coordinated-disclosure window.

## Reporting a vulnerability

**Use GitHub private vulnerability reporting:**
<https://github.com/MAGORS-organisation/Deep-Links-Engine/security/advisories/new>

It is private to the maintainers, it gives the report a tracked identifier, and the eventual
advisory (with a CVE if warranted) is published from the same place. There is no separate
security mailbox and no PGP key at this time; if that changes, this file changes first.

Please **do not** open a public issue, discussion or pull request for anything you believe is a
vulnerability, and do not test against instances you do not operate (see *Scope*).

A useful report contains: the affected component and version (release tag, image digest or
commit), a reproduction (requests, configuration keys that differ from the defaults), the impact
as you understand it, and — if you have one — a suggested fix. Proof-of-concept code is welcome;
exploitation of anybody's data is not.

## What to expect

| Step | Target |
|---|---|
| Acknowledgement | 3 business days |
| Triage and severity (CVSS 3.1) | 10 business days |
| Fix for Critical / High | 30 days from triage |
| Fix for Medium / Low | next scheduled release, at most 90 days |
| Public advisory | when the fix ships, or at **90 days** from the report — whichever is earlier |

The 90-day window is a ceiling, not a target. If a fix needs longer (a protocol change, a
dependency that has no patched release yet), the maintainers will say so before day 60 and ask
for an extension rather than let the deadline pass silently. If a report is being actively
exploited in the wild, the advisory goes out with mitigations before a fix exists.

Reporters are credited in the advisory unless they prefer not to be.

## Supported versions

No release has been tagged yet. Until `v1.0.0`:

| Line | Security fixes |
|---|---|
| `develop` (integration branch) | yes — every fix lands here first |
| latest `v0.x` tag | yes, for the latest minor only |
| older `v0.x` tags | no — upgrade |

After `v1.0.0` the latest minor of the latest major is supported, plus the previous major for six
months after the new major ships. This table is updated with every release.

## Scope

**In scope**

- The edge (`src/Dle.Edge`): resolve, interstitial, `/.well-known` files, QR, rate limiting, bot
  classification, click-stream ingestion.
- The control plane (`src/Dle.Control`): API, authentication (API keys, OIDC), tenant isolation,
  domain verification, webhooks.
- `Dle.Crypto`, `Dle.Domain`, the persistence layers, and the analytics sinks.
- The admin console (`src/Dle.Admin.Web`).
- The SDKs (`sdk/web`, `sdk/android`, `sdk/ios`) — including anything they would leak from a host
  application.
- Deployment assets (`deploy/`): compose stack, Dockerfiles, Caddyfile, Helm chart, Postgres init.
- Build and supply chain: `.github/`, lock files, `Directory.Packages.props`, `NuGet.config`.

Of particular interest, because they are the acceptance criteria in §E.8:

- open redirects, or any way to reach a target that did not pass validation at creation (S-01);
- cross-tenant reads or writes, including by manipulating identifiers (S-02);
- signature or key-comparison paths that are not constant-time (S-03);
- a CSP on the interstitial that admits `unsafe-inline` (S-04);
- rate limits that can be bypassed on any public endpoint (S-07), including the separate 404
  budget that protects against slug enumeration;
- PII reaching logs at `Information` or below (S-08).

**Out of scope**

- Vulnerabilities in third-party dependencies with no DLE-specific exploit path — report those
  upstream; we track them through the Security workflow.
- Misconfiguration of a particular self-hosted instance (weak `DLE_MASTER_SECRET`, an exposed
  Postgres port, a Caddy running with a custom, weaker TLS policy). Configuration guidance is a
  documentation issue, not a vulnerability.
- Denial of service by volume against an instance you do not own. Rate limiting is in scope; load
  testing other people's servers is not.
- Social engineering of maintainers or operators, and physical attacks.
- Findings from automated scanners without a demonstrated impact.
- The absence of a security header on a page that has no state to protect, unless it enables
  something concrete.

## Safe harbour

Research conducted in good faith and within this policy is authorised. The maintainers will not
pursue or support legal action against researchers who: test only against instances they operate
or have explicit permission for; make a good-faith effort to avoid privacy violations, data
destruction and service disruption; do not exfiltrate more data than needed to demonstrate the
issue; and give the maintainers a reasonable time to fix before disclosing. If you are unsure
whether something is covered, ask first through the reporting channel above.

Instance operators set their own rules for their own deployments; this safe harbour is about
this project, not about every server that runs it.

## Abuse reports

A DLE link can point at content that is harmful or illegal. The party responsible for that link
is **the operator of the instance that serves it**, not this project — DLE is software, and the
maintainers neither host links nor can see or remove anybody's.

What the software gives operators to act on a notice, in the sense of Regulation (EU) 2022/2065
(Digital Services Act) **Article 16** — notice-and-action mechanisms:

- `POST /abuse-reports`, rate-limited per §E.9 (no captcha), so that a notice can be submitted for
  any slug without an account. It is served by the control plane, but the shipped Caddy and Helm
  routing send that path to the edge, which does not serve it, and no page links to it: it is not
  reachable from outside today (see [Known gaps](README.md#known-gaps));
- a quarantine state for links and a reputation check on targets (off until a source is
  configured), so that a notified link can be taken down and the decision recorded. The edge keeps
  serving a quarantined link from its cache for up to 10 min 30 s with the default settings;
- an audit trail of who did what to which link, for the statement of reasons the DSA requires.

If you have found a harmful link served by a DLE instance: contact its operator — the domain's
WHOIS, `security.txt` or hosting provider is the way to find them. If the link is served from a
domain this project operates, report it through the vulnerability channel above and mark it *abuse*.

## Hardening the build

Things that are in place, so that a report can say "this should have caught it":

- All dependencies are version-locked (`packages.lock.json`, `package-lock.json`, central package
  management) and restored with `--locked-mode`. Dependabot is configured to propose updates for
  humans to merge (`.github/dependabot.yml`), but has not run yet: GitHub reads that file from the
  default branch, which is still `master` with only the initial commit.
- GitHub Actions are pinned to commit SHAs. Workflows run with least-privilege tokens.
- Every push runs CodeQL, `dotnet list package --vulnerable`, `npm audit`, Trivy over lock files
  and both container images, and a licence policy check (`.github/workflows/security.yml`).
- The release workflow (`.github/workflows/release.yml`) publishes a CycloneDX SBOM for each
  ecosystem, a cryptographic bill of materials (CBOM), SLSA provenance for the images, and
  Sigstore/cosign keyless signatures for images, chart, SBOMs and CBOM, with verification commands
  in the release notes. It runs on a version tag and has not run yet: no release has been tagged.
