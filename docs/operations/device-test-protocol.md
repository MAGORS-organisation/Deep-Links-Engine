# Mobile Device Test Protocol (8 Combinations)

This protocol is the authoritative operational manual for executing the mandatory pre-release manual test matrix defined in [docs/zadanie.md §D.2](../zadanie.md) and [device-test-matrix.md](device-test-matrix.md).

Every release candidate must execute this protocol. A release is blocked until all 8 device combinations pass with verified photographic or screen-recorded evidence ([§D.7 Acceptance Criteria](../zadanie.md)).

---

## 1. Test Environment & Prerequisites

### 1.1 Device Fleet
Tests must be executed on real devices (physical hardware or cloud device slots via BrowserStack App Live / Firebase Test Lab):

| Slot | Device Specification | Minimum OS | Primary Role |
|---|---|---|---|
| **D1** | Apple iPhone 15 / 16 | iOS 18.x | Universal Links, Safari, iMessage, In-App WebView |
| **D2** | Apple iPhone (Secondary) | iOS 18.x / 26 Preview | Deferred deep linking, TestFlight install |
| **D3** | Google Pixel 8 / 9 / Samsung S24 | Android 15 (API 35) | Android App Links, Play Install Referrer |
| **D4** | Android Device (Secondary) | Android 16 / Preview | Facebook In-App Browser, Custom Tabs |
| **D5** | Desktop Workstation | Windows 11 (Chrome 130+) | Desktop web fallback |
| **D6** | Desktop Workstation | macOS 15+ (Safari 18+) | Desktop web fallback |

### 1.2 Tenant & Link Test Configuration
To ensure all routing actions and fallback paths are exercised without production side-effects:

1. **Test Tenant**: Create an isolated tenant (e.g. `qa-matrix-tenant`) with:
   - `consent_mode: "full"`
   - Domain registered and verified: `https://link.qa.example.com`
2. **Registered Applications**:
   - iOS: Bundle ID `com.example.qa.dle`, Team ID `ABCDE12345`, `custom_scheme: "qadle"`
   - Android: Package `com.example.qa.dle`, SHA-256 fingerprint configured in `.well-known/assetlinks.json`, `custom_scheme: "qadle"`
3. **Test Deep Link**:
   - Slug: `matrix-test-link`
   - Target URL: `https://example.com/fallback-destination`
   - `routing_rules`:
     ```json
     [
       {
         "id": "ios-rule",
         "when": { "platform": ["ios"] },
         "then": {
           "action": "app_or_store",
           "store_url": "https://apps.apple.com/app/id000000000",
           "custom_scheme": "qadle://open?screen=promo"
         }
       },
       {
         "id": "android-rule",
         "when": { "platform": ["android"] },
         "then": {
           "action": "app_or_store",
           "store_url": "https://play.google.com/store/apps/details?id=com.example.qa.dle",
           "referrer_template": "dl_cid={click_id}",
           "custom_scheme": "qadle://open?screen=promo"
         }
       },
       {
         "id": "default",
         "then": {
           "action": "web",
           "url": "https://example.com/fallback-destination"
         }
       }
     ]
     ```

---

## 2. Test Execution Scenarios (TC-M01 to TC-M08)

---

### TC-M01: iPhone (iOS 18) — Safari Universal Link (App Installed)

* **Objective**: Verify that tapping an active deep link on iOS 18 opens the installed application directly via Universal Links, and the iOS SDK successfully reports the open event ([FR-223](../zadanie.md)).
* **Preconditions**:
  * Target iOS application installed on device Slot D1.
  * Associated Domains configured with `applinks:link.qa.example.com`.
  * `apple-app-site-association` verified on Apple CDN.
* **Execution Steps**:
  1. Open Safari or Apple Notes on the device.
  2. Paste and tap `https://link.qa.example.com/matrix-test-link`.
  3. Observe immediate transition into the application.
* **Expected Result**:
  * The native app opens immediately without prompt or Safari redirect page.
  * In app logs / network traffic: SDK makes `POST /api/v1/events` recording event `link_open`.
* **Evidence Required**:
  * Screen recording showing the tap and app opening (max 10s).
  * Charles / Proxyman or SDK console log showing `link_open` dispatch.

---

### TC-M02: iPhone (iOS 18/26) — Safari Deferred Deep Link (App NOT Installed)

* **Objective**: Verify the non-installed flow on iOS: interstitial displays, falls back to App Store, and post-install context resolution is verified ([ADR-008](../adr/0008-deferred-deep-linking-strategies.md)).
* **Preconditions**:
  * Application is **uninstalled** from Slot D2.
* **Execution Steps**:
  1. Open Safari and navigate to `https://link.qa.example.com/matrix-test-link?dl_consent=all`.
  2. Observe the interstitial page rendering with branding.
  3. After the redirect timer (or manual CTA click), verify redirection to the App Store test URL.
  4. Sideload / install the build (TestFlight or Xcode).
  5. Launch the application.
* **Expected Result**:
  * Interstitial renders valid HTML with tenant branding.
  * User is redirected to App Store URL.
  * On launch, SDK queries `/api/v1/resolve`.
* **Evidence Required**:
  * Screenshot of the interstitial page in Safari.
  * App Store redirect verification.
  * JSON response from `/api/v1/resolve`.

---

### TC-M03: iPhone (iOS 18) — Instagram In-App Browser Sandbox

* **Objective**: Verify that inside Instagram's sandboxed WebView, the interstitial displays and user interaction successfully triggers app opening ([§A.2.6](../zadanie.md)).
* **Preconditions**:
  * Instagram app installed and logged in.
  * Target QA app installed on device.
* **Execution Steps**:
  1. Send `https://link.qa.example.com/matrix-test-link` via direct message in Instagram.
  2. Tap the link to open Instagram's in-app browser.
  3. Observe the interstitial screen displaying the "Open in App" button.
  4. Tap the "Open in App" CTA button.
* **Expected Result**:
  * Instagram does not fail silently or show a broken redirect page.
  * Interstitial renders with clear button.
  * Tapping button invokes `qadle://open` custom scheme, triggering the native app handoff.
* **Evidence Required**:
  * Screen recording capturing Instagram DM -> in-app browser interstitial -> tap CTA -> app opens.

---

### TC-M04: iPhone — Messages (iMessage) Open Graph Unfurling

* **Objective**: Verify that Apple's link preview fetcher receives HTTP 200 with Open Graph tags without counting a human campaign click ([TC-106](../zadanie.md)).
* **Preconditions**:
  * Active iMessage conversation.
* **Execution Steps**:
  1. Paste `https://link.qa.example.com/matrix-test-link` into an iMessage thread and send.
  2. Wait for iMessage preview bubble generation.
* **Expected Result**:
  * The link renders with a rich preview card (title, image/thumbnail, domain).
  * Control plane / edge click analytics do **not** increment campaign clicks (classified as `is_bot: true`).
* **Evidence Required**:
  * Screenshot of the unfurled message bubble in iMessage.
  * Database query confirming 0 campaign clicks recorded for this fetch.

---

### TC-M05: Android 15 (Chrome) — App Link Direct Open (No Chooser)

* **Objective**: Verify Android 15 App Links opens the app directly without showing the system disambiguation dialog ("Open with Chrome or App?").
* **Preconditions**:
  * Target Android app installed with `autoVerify="true"` in AndroidManifest.xml.
  * Domain `link.qa.example.com` has valid `/.well-known/assetlinks.json` matching the app signing fingerprint.
  * Verify domain association via `adb shell pm get-app-links com.example.qa.dle` reports `verified`.
* **Execution Steps**:
  1. Open Chrome on Android 15 (Slot D3).
  2. Tap or navigate to `https://link.qa.example.com/matrix-test-link`.
* **Expected Result**:
  * Android OS dispatches the intent directly to the target app.
  * No system disambiguation popup ("Open with...") is shown.
* **Evidence Required**:
  * Screen recording of the tap and direct app launch.
  * Output of `adb shell pm get-app-links com.example.qa.dle`.

---

### TC-M06: Android 15 — Google Play Install Referrer Attribution

* **Objective**: Verify deferred deep linking on Android 15 via the Play Install Referrer API delivers `dl_cid` with deterministic confidence 1.0 ([FR-143](../zadanie.md)).
* **Preconditions**:
  * App **uninstalled** from Slot D3.
  * Consent enabled (`?dl_consent=all`).
* **Execution Steps**:
  1. Open Chrome on Android 15 and tap `https://link.qa.example.com/matrix-test-link?dl_consent=all`.
  2. Chrome redirects to `https://play.google.com/store/apps/details?id=...&referrer=dl_cid%3D{click_id}`.
  3. Capture the exact `{click_id}` passed in the `referrer` query parameter.
  4. Sideload / install the application.
  5. Launch the application; observe SDK reading the referrer string via `InstallReferrerClient`.
  6. SDK calls `POST /api/v1/resolve` with `referrer: "dl_cid=..."`.
* **Expected Result**:
  * Resolve endpoint returns HTTP 200 with:
    * `match_type: "install_referrer"`
    * `confidence: 1.0`
    * Correct deep link destination context and parameters.
* **Evidence Required**:
  * HTTP traffic or logcat trace showing the referrer string.
  * Response JSON from `/api/v1/resolve`.

---

### TC-M07: Android 16 — Facebook In-App Browser (Custom Tab Handoff)

* **Objective**: Verify that inside the Facebook Android in-app browser, the link renders an interstitial that properly hands off into Chrome Custom Tabs and activates the App Link ([§A.2.6](../zadanie.md)).
* **Preconditions**:
  * Facebook app installed on Android 16 (Slot D4).
  * Target application installed on device.
* **Execution Steps**:
  1. Post or send `https://link.qa.example.com/matrix-test-link` inside Facebook.
  2. Tap the link in Facebook feed/message.
  3. In Facebook's in-app browser, observe interstitial.
  4. Tap "Open in App" CTA button.
* **Expected Result**:
  * Facebook in-app browser transitions via Custom Tab / intent scheme to the native app.
  * No blank white screen or blocked navigation error.
* **Evidence Required**:
  * Screen recording showing Facebook in-app browser transition to native application.

---

### TC-M08: Desktop (Windows / macOS) — Web Fallback Only

* **Objective**: Verify that on desktop browsers (Windows 11 Chrome, macOS Safari), the link performs a clean web redirect without attempting to open mobile apps or launching broken custom schemes ([TC-101](../zadanie.md)).
* **Preconditions**:
  * Slot D5 (Windows 11, Chrome) and Slot D6 (macOS, Safari).
* **Execution Steps**:
  1. Open browser on desktop.
  2. Navigate to `https://link.qa.example.com/matrix-test-link`.
* **Expected Result**:
  * Edge issues an HTTP 302 redirect directly to `https://example.com/fallback-destination`.
  * Browser lands cleanly on the web destination.
  * No dialog ("Open Application?") or broken custom protocol error is displayed.
* **Evidence Required**:
  * Network waterfall screenshot showing `HTTP 302 -> HTTP 200 (target_url)`.

---

## 3. Automated Social Crawler Verification Check

In addition to manual tests, execute the automated crawler suite before every release:

```bash
# Verify all 6 crawlers (Facebook, Twitter, Slack, LinkedIn, Discord, WhatsApp)
python3 .github/scripts/verify-crawlers.py https://link.qa.example.com/matrix-test-link
```

Criteria: All 6 crawlers must report `✅ PASS` with HTTP 200 and valid Open Graph meta tags.

---

## 4. Release Run Sheet & Evidence Archive

Record results in the following sign-off table prior to cutting the release:

| # | Test Scenario | Device Model | OS Build | Result | Evidence File / Link | Tester |
|---|---|---|---|---|---|---|
| **1** | iOS 18 Safari Universal Link | iPhone 15 Pro | iOS 18.2 (22C152) | [PASS / FAIL] | `evidence/rc1-tc01-universal-link.mp4` | |
| **2** | iOS Safari Deferred Deep Link | iPhone 14 | iOS 18.1 (22B83) | [PASS / FAIL] | `evidence/rc1-tc02-deferred.png` | |
| **3** | iOS Instagram In-App Browser | iPhone 15 Pro | iOS 18.2 | [PASS / FAIL] | `evidence/rc1-tc03-instagram.mp4` | |
| **4** | iOS iMessage Preview Unfurling | iPhone 15 | iOS 18.2 | [PASS / FAIL] | `evidence/rc1-tc04-imessage.png` | |
| **5** | Android 15 App Link (No Chooser)| Pixel 9 | Android 15 (AP2A) | [PASS / FAIL] | `evidence/rc1-tc05-applink.mp4` | |
| **6** | Android 15 Install Referrer | Pixel 8 | Android 15 (AP2A) | [PASS / FAIL] | `evidence/rc1-tc06-referrer.json` | |
| **7** | Android 16 Facebook In-App | Pixel 9 Pro | Android 16 Dev | [PASS / FAIL] | `evidence/rc1-tc07-facebook.mp4` | |
| **8** | Desktop Web Fallback | Windows 11 / Mac | Chrome 131 / Saf 18| [PASS / FAIL] | `evidence/rc1-tc08-desktop.png` | |
| **--**| Crawler Automated Check | CLI Script | Python 3.12 | [PASS / FAIL] | `evidence/rc1-crawlers.log` | |

**Sign-off:**
* QA Lead: `___________________` Date: `__________`
* Release Owner: `___________________` Date: `__________`
