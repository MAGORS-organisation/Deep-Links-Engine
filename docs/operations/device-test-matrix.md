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
