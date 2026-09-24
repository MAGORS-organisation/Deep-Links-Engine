# Device test matrix

**What this is:** the manual, real-device regression that must pass before every release, and why it
cannot be automated away. **Who it is for:** QA and the release owner. Source:
[docs/zadanie.md §D.2](../zadanie.md). The checklist form with evidence fields is in
[release-checklist.md](release-checklist.md).

## Why this exists

The most expensive and most fragile part of this system is outside its code: in iOS, Android, the app
stores and the in-app browsers of Facebook, Instagram and TikTok, all of which change without notice.
The test strategy is therefore many cheap automated tests on our side and a small, uncompromising
manual matrix on the platforms' side ([§D.1](../zadanie.md)).

## The eight combinations

| # | Device / OS | Opening context | What is being verified |
|---|---|---|---|
| 1 | iPhone, iOS 18 | Safari, tap on link | Universal Link opens the app directly; the SDK reports the open (without the report the click is invisible — [FR-223](../zadanie.md)) |
| 2 | iPhone, iOS 26 | Safari, app not installed | Interstitial → App Store → after install the deferred context arrives through a deterministic path (claim code or login), and the result carries the right `match_type` and confidence |
| 3 | iPhone, iOS 18 / 26 | Instagram in-app browser | The interstitial renders and its **anchor** opens the app. Universal Links do not fire on page load inside these webviews; only a real tap on an anchor does ([§A.2.6](../zadanie.md)) |
| 4 | iPhone | Messages (iMessage) | The link shows a rich preview from the OG tags, not bare text — the crawler path returned 200 HTML rather than a redirect |
| 5 | Android 15, Chrome | tap on link | App Link opens the app with no app chooser: the domain is verified and the fingerprint is the Play App Signing one |
| 6 | Android 15 | App not installed, Play → install | The Install Referrer delivers `dl_cid`; attribution is `install_referrer` with confidence 1.0 |
| 7 | Android 16 | Facebook in-app browser | Interstitial, then a Custom Tab, then the App Link — Custom Tabs honour App Links, a bare WebView does not |
| 8 | Desktop, Windows and macOS, Chrome and Safari | tap on link | Web fallback only; no attempt to open an app |

The expected outcomes above are the release criteria and stay as written. Several rows cannot produce
them with the engine as it is today ([README — Known gaps](../../README.md#known-gaps)).

**Test configuration** (for this matrix only, not a production recommendation):

- **Link.** One `app_or_store` rule per platform (`when.platform`), each with its **own** `store_url` —
  the app's registered `store_url` is not used as a fallback — the Android one with
  `referrer_template: "dl_cid={click_id}"`, plus a `web` default rule. A link created without
  `routing_rules` gets only a `web` default rule: every client, the in-app browsers of rows 3 and 7
  included, is redirected to `target_url`, never to the store and never to an interstitial.
- **Apps.** A `custom_scheme` on each registered app. The interstitial's "open in app" button is a
  custom-scheme URL, and it is left out when the app has none.
- **Row 6.** The tenant's `consent_mode` set to `full`, and the link opened with `?dl_consent=all`
  appended — the only click-time consent signal the edge reads (a browser sending `Sec-GPC: 1`
  overrides it). Without both, `{click_id}` is dropped from the Play referrer and `/v1/resolve`
  answers `none` with `consent_missing`.

**What the affected rows do today:**

| # | Today |
|---|---|
| 1, 5 | Can pass: the app opens and can report the open. But it receives only the short URL — nothing expands a slug into its `deeplink_path` — so it lands on the target screen only if it parses a human-readable slug itself. Record where it landed |
| 2 | No deterministic iOS path works: the edge never issues or shows a claim code, and login matching reads a click field that nothing writes. Expect `match_type: none` |
| 3 | Passes only with the test configuration, and then through the custom scheme — the anchor is never a Universal Link |
| 6 | Passes only with the test configuration (tenant `full`, `dl_consent=all` on the click) |
| 7 | The interstitial's button is a custom-scheme URL, not an App Link, so the "Custom Tab, then App Link" hand-off this row verifies cannot happen |

Plus the crawler check, which is automatable and is also run in CI: `facebookexternalhit`,
`Twitterbot`, `Slackbot`, `LinkedInBot`, `Discordbot`, `WhatsApp` get HTTP 200 with OG tags and no
campaign click is recorded.

## Why it cannot be automated

- **Universal Links behave differently in the iOS simulator.** The association file is loaded
  differently and `?mode=developer` changes the behaviour, so a passing simulator run proves nothing
  about a device.
- **In-app browsers cannot be scripted.** There is no reliable way to open a link inside Instagram
  programmatically in a way that matches what a user does.
- **Domain verification is delayed.** Apple's CDN takes up to 24 hours and devices refresh weekly;
  Android 15+ takes up to seven days. A "change the rule and verify" test takes days, not minutes.

## Cost and cadence

Budget **one day of manual regression per release**, with a written checklist and photographic or
screen-recorded evidence per row. Devices: at least one physical iPhone and one physical Android, or a
device cloud such as BrowserStack App Live or Firebase Test Lab (roughly 50–200 EUR per month). This is
a fixed operating cost of the project, not a one-off, and the specification's advice is to double the
first instinct for client-side QA effort ([§F.3](../zadanie.md)).

## Recording a run

For each row: device model, OS build, app version, engine version, tester, date, pass/fail, and the
evidence file. Keep the runs; when Apple or Google change something, the history is what tells you when
it broke.
