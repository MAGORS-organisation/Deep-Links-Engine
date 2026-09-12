# Release checklist

**What this is:** the acceptance criteria for a release, as a checklist with evidence fields. **Who it
is for:** whoever cuts the release. Source: [docs/zadanie.md §D.7](../zadanie.md) and the Definition of
Done in §C.7. Print it or copy it into the release issue; a release without the evidence columns filled
in is not done.

Release: `v[x.y.z]`   Date: `[…]`   Release owner: `[…]`

## Automated gates (from CI, attach the run URLs)

| # | Gate | Evidence |
|---|---|---|
| 1 | `ci.yml` green: locked-mode restore, `-warnaserror` build, format check, all four suites with `DLE_TESTS_REQUIRE_DOCKER=1` | `[run URL]` |
| 2 | Coverage: domain tier ≥ 90 % hard; overall reported | `[numbers]` |
| 3 | `security.yml` green: no vulnerable NuGet packages, npm audit, Trivy, licence policy, ZAP baseline | `[run URL]` |
| 4 | `sbom.yml`: SBOM (.NET + npm) and CBOM generated and validated, attached to the release | `[artefact URL]` |
| 5 | `release.yml`: images built multi-arch, signed with cosign, Helm chart pushed | `[run URL]` |
| 6 | Load profile (§D.5) passed: p95 < 25 ms, p99 < 50 ms, failure rate < 0.1 %, cache hit ≥ 95 %, `dle_click_events_dropped_total` = 0 | `[k6 summary]` |
| 7 | Migration applied and rolled back on a copy of a production-shaped database | `[who, when, notes]` |

## Manual gates

| # | Gate | Evidence |
|---|---|---|
| 8 | No open critical or high security findings | `[tracker query]` |
| 9 | Self-hosting quick start followed from zero on a clean machine by someone who did not write it — the "first 30 minutes" test | `[name, time taken, friction noted]` |
| 10 | All demo domains passed nightly verification three nights in a row | `[dates]` |
| 11 | CHANGELOG updated; anything never compiled or never run is still stated plainly | `[commit]` |
| 12 | Device matrix below completed with photographs or screen recordings | see below |

## Device matrix (§D.2.1) — every row needs evidence

| # | Device / OS | Opening context | What must happen | Result | Evidence |
|---|---|---|---|---|---|
| 1 | iPhone, iOS 18 | Safari, tap on link | Universal Link opens the app; SDK reports `link_open` | `[pass/fail]` | `[…]` |
| 2 | iPhone, iOS 26, app **not** installed | Safari | Interstitial → App Store → after install, deferred context via claim code or login | `[…]` | `[…]` |
| 3 | iPhone, iOS 18 or 26 | **Instagram in-app browser** | Interstitial shown; its button opens the app | `[…]` | `[…]` |
| 4 | iPhone | **Messages (iMessage)** | Link renders with a preview (OG data), not as plain text | `[…]` | `[…]` |
| 5 | Android 15, Chrome | tap on link | App Link opens the app with no chooser | `[…]` | `[…]` |
| 6 | Android 15, app **not** installed | Play → install | Install Referrer delivers `dl_cid`; attribution is `install_referrer`, confidence 1.0 | `[…]` | `[…]` |
| 7 | Android 16 | **Facebook in-app browser** | Interstitial, custom tab, App Link | `[…]` | `[…]` |
| 8 | Desktop (Windows/macOS, Chrome + Safari) | tap on link | Web fallback; no attempt to open an app | `[…]` | `[…]` |

Crawler check (automatable, run it anyway): `facebookexternalhit`, `Twitterbot`, `Slackbot`,
`LinkedInBot`, `Discordbot`, `WhatsApp` each receive HTTP 200 with OG tags and no click is counted in
campaign statistics. `[result]`

## Sign-off

| Role | Name | Date |
|---|---|---|
| Release owner | | |
| QA | | |
| Security | | |
