# Routing rules

**What this is:** the reference for the rule language stored in `links.routing_rules` — every predicate, every action, how evaluation works, and what the validator rejects. Derived from [`src/Dle.Domain/Routing`](../../src/Dle.Domain/Routing), which is the authority.
**Who it is for:** marketers and integrators writing rules through the API or the console, and engineers extending the engine.

A JSON Schema that validates the [§B.5.4](../zadanie.md#b54-schéma-pravidiel-routovania-linksrouting_rules) example is at [routing-rules.schema.json](routing-rules.schema.json). The schema checks shape; the validator (below) checks the things a schema cannot, such as "the default rule must be last".

## Principles

- **Rules are data, not code.** They are evaluated deterministically, **in order**, and the **first match wins** (FR-127).
- **Exactly one default rule, and it is last.** The default rule is the one without `when`; it matches every client. A rule set without one cannot be saved, because a client that matched nothing would otherwise get `404` for a link the marketer believes is live (TC-105). Rules after the default can never match, so the validator rejects them.
- **The action describes intent, not the HTTP response.** The same `app_or_store` produces an interstitial in an in-app webview and a plain `302` on the desktop ([ADR-0009](../adr/0009-http-response-shape-never-301.md)). Turning intent into a response class is the engine's job, not the rule author's.
- **No randomness anywhere.** The A/B bucket is a pure function of the click id, so a decision replays identically on every node, in the rule simulator (FR-129), and months later from the click stream.
- Wire format is `snake_case`. Limits: at most **50 rules** per link, at most **64 KiB** of JSON.

## Rule

```json
{ "id": "r1", "when": { …condition… }, "then": { …action… } }
```

| Field | Required | Meaning |
|---|---|---|
| `id` | yes | Stable identifier, unique within the link. Written to the click stream as the matched rule id — **renaming it breaks historical reports** |
| `when` | no | The condition. Omitted (or `null`) marks the **default rule** |
| `then` | yes | The action |

## Condition (`when`)

Every property is optional. A property left out (or an empty array) means "do not care". Within one property the listed values are **OR**-ed; across properties the results are **AND**-ed. Comparisons are culture-independent.

| Property | Type | Matches when… | Notes |
|---|---|---|---|
| `platform` | `string[]` | the client platform is one of `ios`, `android`, `desktop`, `other`, `unknown` | lowercase |
| `os_version` | version predicate | the reported OS version satisfies every bound set | see below; a predicate with any bound never matches a client whose version is unknown (FR-124) |
| `app_version` | version predicate | the host app version reported by the SDK satisfies every bound | as above |
| `country` | `string[]` | GeoIP country is one of the listed ISO-3166-1 alpha-2 codes, upper case (`"SK"`) | if the GeoIP file is missing, country is `null` and geo rules fall through to the default ([§D.6](../zadanie.md#d6-chaos-a-odolnosť)) |
| `region` | `string[]` | sub-national region, in the GeoIP provider's notation | |
| `language` | `string[]` | the client's primary language subtag, lowercase; `"sk"` also matches a client announcing `sk-SK` | |
| `channel` | `string[]` | the classified channel is one of the canonical names below | |
| `time_window` | object | every component that is set holds, evaluated in **UTC** (FR-126) | see below |
| `ab` | `AbVariant[]` | the click's bucket falls inside one of the variants' consecutive percentage ranges | see below |

**Canonical channel names** (`ChannelNames.cs`; part of the public wire contract, they never change): `unknown`, `browser`, `crawler`, `in_app_fb`, `in_app_ig`, `in_app_tiktok`, `in_app_linkedin`, `in_app_snapchat`, `in_app_x`, `in_app_whatsapp`, `in_app_telegram`, `in_app_pinterest`, `in_app_other`, `app`. Everything `in_app_*` is an in-app webview. A webview is served an interstitial when the matched rule's action is `app_or_store`, whatever that rule's `interstitial` mode says; for every other action it gets what the action table below says, which for `web` and `store_only` is a `302`.

### Version predicate

```json
{ "gte": "17.0", "lt": "18" }
```

Keys: `eq`, `gt`, `gte`, `lt`, `lte`; any combination, all must hold. Missing components count as zero (`"17"` = `17.0.0`), a pre-release suffix is ignored. A predicate with no keys matches anything, including an unknown version.

### Time window

```json
{ "from": "2026-09-01T00:00:00Z", "to": "2026-09-30T23:59:59Z", "hours_utc": [8, 9, 10], "days_of_week_utc": [1, 2, 3, 4, 5] }
```

`from`/`to` are instants; `hours_utc` are 0–23; `days_of_week_utc` are 0 (Sunday) to 6 (Saturday). All in UTC, so the rule behaves the same on every node regardless of server locale.

### A/B split

```json
"ab": [ { "variant": "a", "percent": 50 }, { "variant": "b", "percent": 50 } ]
```

The bucket is **FNV-1a over the UTF-8 bytes of `click_id`, modulo 100** (`ConsistentBucket.Of`), giving 0–99. Variants are laid out as consecutive ranges: with the example above, buckets 0–49 are `a`, 50–99 are `b`. The percentages may sum to less than 100; a bucket beyond the accumulated total **does not match this rule** and evaluation continues with the next one — so `[{ "variant": "pilot", "percent": 10 }]` routes 10 % of clicks through this rule and 90 % onward. The matched variant is reported in analytics. FNV-1a is deliberately not a cryptographic hash; it is tiny, allocation-free and stable across runtimes.

## Action (`then`)

| Field | Type | Required for | Meaning |
|---|---|---|---|
| `action` | enum | always | `web`, `app_or_store`, `store_only`, `app_only`, `block` |
| `url` | absolute `http(s)` URL | `web` | the web target. For other actions, when absent, the link's own `target_url` is the web fallback |
| `deeplink_path` | path | — | in-app path handed to the app, e.g. `/product/123`. **A path, never a URL**, and never taken from the incoming request (TC-164) |
| `store_url` | absolute `http(s)` URL | `app_or_store`, `store_only` | App Store / Play URL; existing campaign parameters (`pt`, `ct`, `mt`, `id`) are preserved |
| `referrer_template` | string | — | Android only: template for the Play Install Referrer. Placeholders `{click_id}`, `{utm_source}`, `{utm_medium}`, `{utm_campaign}`, `{utm_term}`, `{utm_content}`, `{link_id}` |
| `interstitial` | enum | — | `auto` (default), `always`, `never` |

| `action` | Intent | What the engine does per client ([`DecisionKind`](../../src/Dle.Domain/Routing/DecisionKind.cs)) |
|---|---|---|
| `web` | send to a web page | `302` to `url` |
| `app_or_store` | try the app, fall back to the store | interstitial for in-app webviews (and, per `interstitial`, mobile browsers); otherwise `302` to the store URL with campaign/referrer parameters; desktop gets the web fallback |
| `store_only` | always the store | `302` to `store_url`, in-app webviews included |
| `app_only` | open the installed app, never offer the store | deep link only; web fallback if the app cannot be opened |
| `block` | serve nothing | `Blocked` — used for geo blocking, abuse containment and kill switches |

**Interstitial modes.** They apply to `app_or_store` only; the other actions ignore `interstitial`. `auto`: interstitial for in-app webviews and mobile, redirect for desktop. `always`: interstitial even for an ordinary mobile browser. `never`: no interstitial where a redirect actually works — **in-app webviews still get it**, because a Universal Link fires in a webview only on a genuine tap on an `<a>` element ([§A.2.6](../zadanie.md#a26-in-app-prehliadače-a-crawlery)).

**Status (2026-09-24).** A link created without `routing_rules` gets a single `web` default rule: always a `302` to `target_url`, never the store and never an interstitial — also inside Instagram and Facebook webviews. `app_or_store` and `store_only` rules must carry their own `store_url`; the app-level store URL is not used as a fallback. The interstitial's "open in app" button is a custom-scheme URL (never a Universal Link / App Link, because `deeplink_path` must be relative), or is absent when the app has no custom scheme. See [Known gaps](../../README.md#known-gaps).

## Store URLs and the install referrer

`RoutingUrlBuilder` assembles the final URL. What it will and will not carry:

- **Forwardable query keys.** Only these keys from the *incoming* request are copied onto the target, percent-encoded: `utm_source`, `utm_medium`, `utm_campaign`, `utm_term`, `utm_content`, `gclid`, `fbclid`, `ttclid`, `msclkid`, `twclid`, `li_fat_id`, `igshid`, `ref`. Everything else is dropped. The link's own `utm` defaults are applied first and are not overridden by the request.
- **Click id.** Appended as `dl_cid=<click_id>` on web targets — only when the consent decision allows click-id linking. Consent gates the click id and nothing else.
- **Android referrer.** The `referrer` parameter on the Play URL is rendered from `referrer_template` (default `dl_cid={click_id}&utm_source={utm_source}&utm_medium={utm_medium}&utm_campaign={utm_campaign}`), URL-encoded once, and capped at **500 encoded characters**. This string is what the SDK reads back through `InstallReferrerClient` ([request-flows.md](request-flows.md#2-deferred-deep-link--android-deterministic-b62)).
- **iOS.** The App Store `ct` campaign parameter on `store_url` is preserved; there is no referrer equivalent, which is why the iOS flow is designed around claim codes and login ([ADR-0008](../adr/0008-deferred-deep-linking-strategies.md); neither works end to end today, see its status note).
- **Schemes.** Only `http` and `https` may appear as a redirect target. `javascript:`, `data:`, `vbscript:`, `file:`, `blob:`, `about:` and `intent:` are rejected — at validation time for rule URLs, and again at build time for anything derived.

## What the validator rejects

`RoutingRuleValidator.Validate` runs in the control plane before a link is stored, **never on the hot path**, and returns every problem at once so the API can answer with one RFC 9457 document. Each error carries a JSON-pointer-like path in wire names (`rules[2].then.url`) and an English message.

| Path | Rejected when |
|---|---|
| `rules` | the set is empty or `null` — "at least one rule, and one of them must be the default rule" |
| `rules` | more than **50** rules |
| `rules` | no default rule (no rule omits `when`) |
| `rules` | more than one default rule |
| `rules[i]` | a rule follows the default rule — it could never match |
| `rules[i]` | the rule is `null` |
| `rules[i].id` | missing, or duplicated within the link |
| `rules[i].then` | missing |
| `rules[i].then.action` | not one of `web`, `app_or_store`, `store_only`, `app_only`, `block` |
| `rules[i].then.interstitial` | not one of `auto`, `always`, `never` |
| `rules[i].then.url` | action is `web` and the URL is missing, relative, or not `http`/`https` |
| `rules[i].then.store_url` | action is `app_or_store` or `store_only` and the store URL is missing, relative, or not `http`/`https` |
| `rules[i].then.deeplink_path` | contains `://`, or starts with `//` or `\\` — it must be a path such as `/product/123` |
| `rules[i].when.ab[v]` | the variant is `null`; `variant` name missing or duplicated within the rule; `percent` outside 1–100 |
| `rules[i].when.ab` | the percentages sum to more than 100 |
| `rules[i].when.time_window` | `to` is not later than `from`; an `hours_utc` entry outside 0–23; a `days_of_week_utc` entry outside 0–6 |

The 64 KiB size limit is enforced by the API layer before parsing.

## Worked example

The [§B.5.4](../zadanie.md#b54-schéma-pravidiel-routovania-linksrouting_rules) rule set (reproduced in [data-model.md](data-model.md#routing-rules-b54)) reads: iOS 17+ in Slovakia or Czechia → app or App Store with the autumn promo path and an interstitial decided by client; any Android → app or Play with a referrer carrying the click id and campaign; everyone else → the web page. An iOS 16 device in Slovakia matches neither `r1` (version) nor `r2` (platform) and gets the default. An Instagram webview on Android matches `r2` and receives the interstitial regardless of `interstitial`, because the channel is `in_app_ig`.

## Where this is verified

The engine, the validator, `ConsistentBucket` and `RoutingUrlBuilder` sit in `Dle.Domain`, the tier held at ≥ 90 % coverage by the unit suite (counts in the CI summary); the API's RFC 9457 mapping of validator errors is covered by the contract suite. The rule simulator's parity with the edge on real devices is part of the pending manual matrix.
