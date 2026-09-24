# Deep Link Engine — Android SDK

Android client for the [Deep Link Engine](https://github.com/MAGORS-organisation/Deep-Links-Engine):
deferred deep linking through the Google Play Install Referrer, App Link reporting and batched
event delivery against the DLE SDK plane (`dle-control`, `POST /v1/resolve` and `POST /v1/events`).

| | |
|---|---|
| Coordinates | `sk.magors.dle:dle-sdk:0.1.0` |
| Package | `sk.magors.dle` |
| minSdk / compileSdk | 23 / 35 |
| Kotlin / JVM | 2.0 / 17 |
| Runtime dependencies | OkHttp, kotlinx-coroutines, kotlinx-serialization, `com.android.installreferrer` |
| Optional | `androidx.security:security-crypto` (encrypted install id when the app ships it) |
| Licence | MIT |

Thin by design (spec §C.5): every dependency of an SDK becomes a dependency of every application
that integrates it, so the list above is the whole list.

## 1. Add the dependency

Nothing is published yet: the artifact is not on Maven Central (the build has no Maven Central
configuration) and the repository has no Gradle wrapper (`gradlew` and its JAR are not committed; only
`gradle/wrapper/gradle-wrapper.properties`, which pins Gradle 8.11.1). Build it from a checkout with a
locally installed Gradle 8.11.1, the way CI does, and publish it to your local Maven repository
([Known gaps](../../README.md#known-gaps)):

```sh
cd sdk/android
gradle :dle-sdk:publishToMavenLocal      # → ~/.m2/repository/sk/magors/dle/dle-sdk/0.1.0/
```

```kotlin
// settings.gradle.kts
dependencyResolutionManagement {
    repositories {
        google()
        mavenCentral()
        mavenLocal()
    }
}

// app/build.gradle.kts
dependencies {
    implementation("sk.magors.dle:dle-sdk:0.1.0")
    // Optional. With it, the install id lives in EncryptedSharedPreferences; without it, in a
    // private SharedPreferences file. The SDK detects the library at runtime.
    implementation("androidx.security:security-crypto:1.1.0-alpha06")
}
```

The SDK's manifest contributes `INTERNET`. It asks for nothing else.

## 2. Initialise

Once, in `Application.onCreate`, so the SDK sees the very first activity and can report a cold
start through an App Link:

```kotlin
class App : Application() {
    override fun onCreate() {
        super.onCreate()
        Dle.initialize(this, dleConfig("https://links.example.sk", "dle_pk_…") {
            consent(DleConsent.denied())      // until the person decides; see §4
            logLevel(DleLogLevel.WARN)
        })
    }
}
```

Java:

```java
Dle.initialize(this, DleConfig.builder("https://links.example.sk", "dle_pk_…")
        .consent(DleConsent.denied())
        .build());
```

`endpoint` is the base URL of `dle-control` (default port 8081). It must be `https`; plain `http`
is accepted only for `localhost`, `127.0.0.1` and the emulator host `10.0.2.2`. The SDK key is the
publishable key from the console; it ships inside the binary by design and is never logged.

Every option is documented on `DleConfig.Builder`. The ones you are most likely to touch:

| Option | Default | Meaning |
|---|---|---|
| `autoReportOpens` | `true` | Observe activities and report App Link opens without any call from you |
| `probabilisticSignals` | `false` | Opt in to sending coarse device signals for probabilistic matching (still gated on consent) |
| `requireAnalyticsConsentForEvents` | `true` | Drop behavioural events without analytics consent |
| `referrerTimeoutMillis` | 8000 | How long a resolve waits for the Play Store before proceeding without a referrer |
| `flushDelayMillis` / `maxQueueSize` | 3000 / 500 | Event batching and the offline buffer bound |
| `okHttpClient` | private | Share your client (certificate pinning goes here; the SDK disables redirects on its copy) |

## 3. Resolve the deferred link

On the first screen, once:

```kotlin
lifecycleScope.launch {
    try {
        val link: DeferredLink = Dle.resolve()
        if (link.isDeterministic) {
            // Never route on a raw string. Validate deeplink_path like any other deep link.
            allowlist.parsePath(link.link?.deeplinkPath)?.let { safe -> router.open(safe) }
        }
    } catch (e: DleException) {
        // Network, timeout, HTTP or malformed body. e.isRetriable says whether trying later helps.
    }
}
```

Java, with the callback overload (delivered on the main thread):

```java
Dle.resolve(new DleCallback<DeferredLink>() {
    @Override public void onSuccess(DeferredLink link) { /* ... */ }
    @Override public void onFailure(DleException error) { /* ... */ }
});
```

What happens on the first call:

1. The Google Play Install Referrer is read **exactly once** per installation and the outcome
   persisted, whatever it was (`OK`, not supported, permission error, unavailable after retries,
   timeout). Play only answers for a limited time after installation and a second read can never
   say more than the first.
2. `POST /v1/resolve` goes out with `platform: "android"`, the app version (from `PackageInfo`),
   the OS version, the raw referrer, the recorded consent, and any evidence you passed.
3. The answer is persisted. **Every later call returns it without a request** (TC-143). A referrer
   without `dl_cid` is an organic install: the engine answers `match_type: "none"`, and that is a
   normal answer, not an error (TC-142).

**Deferred deep linking is off by default on the engine side.** The edge writes `dl_cid` into the
Play referrer only when the tenant's consent mode is `full` and the click itself carries attribution
consent (`dl_consent=all` or `gdpr=0` on the short URL — asserted by whoever built the link; the
interstitial collects none). Under the default `aggregate_only` every install resolves to `none`
([Known gaps](../../README.md#known-gaps)).

Two things make the SDK ask again: a non final answer (`expires_in > 0`) that has aged out, and a
call carrying new deterministic evidence while the stored answer is not deterministic:

```kotlin
// S3 — the user typed the code from the interstitial page. Normalised like the server does.
Dle.resolve(ResolveOptions.claimCode("ACD-EFG"))

// S2 — the user signed in; pass the same keyed hash your website recorded on the click.
Dle.resolve(ResolveOptions.loginKey(hashedAccountId))
```

Neither supplement matches anything today: the edge never issues or shows a claim code, and login
matching reads a click field nothing writes ([Known gaps](../../README.md#known-gaps)). Only the SDK side
is implemented.

A malformed claim code fails locally with `DleException.InvalidArgument`. An unknown, consumed or
expired one comes back as `DleException.Http` with `isClaimCodeInvalid == true`; read
`problem.reason` and `problem.canReissue` before deciding whether to show the entry screen again.

**Read `matchType` and `confidence` together.** `MatchType.PROBABILISTIC` is a hint with a
confidence below 1.0 and `isDeterministic == false`; never grant anything irreversible on it.

## 4. Consent

The SDK never infers consent. Until you call `setConsent`, `DleConfig.consent` applies, and its
default is `denied()`. A recorded decision is persisted with its timestamp and sent as the
`consent` member of the resolve request:

```kotlin
Dle.setConsent(DleConsent.granted())          // analytics + attribution
Dle.setConsent(DleConsent.analyticsOnly())    // behavioural events, no device signals
Dle.setConsent(DleConsent.denied().copy(timestampMillis = System.currentTimeMillis()))
```

- `attribution == false` (or `probabilisticSignals` off): the `signals` object is **omitted** from
  the request, not sent empty (TC-145, TC-146), and the signals are not even collected.
- `analytics == false`: `first_open`, `session`, `conversion` and `custom` events are dropped at
  `trackEvent` time, never buffered. `link_open` is exempt (FR-223).

## 5. App Links

Direct opens are the events most worth reporting: a verified App Link opens your application
straight from the operating system and **no request ever reaches the engine**. Without the SDK's
`link_open` report the click is invisible, and the campaigns that work best are the ones counted
least (FR-223, spec §B.6.4).

### 5.1 Intent filter

```xml
<activity android:name=".MainActivity" android:exported="true" android:launchMode="singleTop">
    <intent-filter android:autoVerify="true">
        <action android:name="android.intent.action.VIEW" />
        <category android:name="android.intent.category.DEFAULT" />
        <category android:name="android.intent.category.BROWSABLE" />
        <data android:scheme="https" />
        <data android:host="link.example.sk" />
    </intent-filter>
</activity>
```

`https` only, exact host. Do not add a custom scheme: any application can register the same one,
and the SDK never puts a token, a code or personal data on a custom-scheme URL (FR-227).

### 5.2 `assetlinks.json`

`dle-edge` serves `https://link.example.sk/.well-known/assetlinks.json` from the console's
configuration. It needs your package name and the SHA-256 of the certificate the APK is signed
with:

```json
[{
  "relation": ["delegate_permission/common.handle_all_urls"],
  "target": {
    "namespace": "android_app",
    "package_name": "sk.example.app",
    "sha256_cert_fingerprints": ["AB:CD:…"]
  }
}]
```

**The Play App Signing trap (FR-144, spec §A.2.2).** With Play App Signing, the certificate Google
signs the shipped APK with is *not* your upload keystore. A fingerprint taken from
`keytool -list -v -keystore upload.jks` verifies nothing: users install an APK signed with a
different key, verification fails silently, and every link opens in the browser. Take the
fingerprint from **Play Console → Test and release → App integrity → App signing key certificate**,
and list both it and the upload certificate if you also distribute unsigned-by-Play builds
(internal testing tracks, debug builds).

**Propagation takes time.** Android verifies at install time by fetching the file, and Google's
verification infrastructure caches results; after a change to `assetlinks.json` expect **up to
seven days** before every device agrees. Do not ship a campaign on the day you change the file.

### 5.3 Checking on a device

```sh
# What the system believes about your domains, per package and per user
adb shell pm get-app-links sk.example.app

# Force a fresh verification (Android 12+), then read the state again
adb shell pm verify-app-links --re-verify sk.example.app
adb shell pm get-app-links sk.example.app

# On Android 11 and older
adb shell dumpsys package domain-preferred-apps

# Google's checker, from the outside
https://digitalassetlinks.googleapis.com/v1/statements:list?source.web.site=https://link.example.sk&relation=delegate_permission/common.handle_all_urls
```

`verified` is the state you want. `1024` or `legacy_failure` means the file was unreachable or the
fingerprint did not match; `none` means verification has not run for that user.

### 5.4 Reporting the open

With `autoReportOpens` (the default) the SDK watches activity creation and resumption and reports
each `ACTION_VIEW` `http(s)` intent once. One requirement on your side: in a `singleTop` or
`singleTask` activity, call `setIntent(intent)` in `onNewIntent`, otherwise the resumed activity
still carries the old intent and the new link is neither reported nor visible to you.

Without `autoReportOpens`, call it yourself:

```kotlin
override fun onCreate(savedInstanceState: Bundle?) { …; route(Dle.handleIntent(intent)) }
override fun onNewIntent(intent: Intent) { super.onNewIntent(intent); setIntent(intent); route(Dle.handleIntent(intent)) }
```

`handleIntent` returns an `AppLinkOpen` with the URL as delivered and the URL as reported (scheme,
host and path; query and fragment are dropped on the device). Each intent is reported once however
often it is handed in, so calling it in addition to the automatic reporting is harmless. Every
`ACTION_VIEW` intent with `http(s)` data is reported; the SDK has no host list of its own.

The URL is the short link as tapped (`https://link.example.sk/aB3xK9pQ`), not the link's
`deeplink_path`: nothing on the SDK plane expands a slug, and an SDK key cannot read the link API.
Your app can open the right screen only when the operator uses readable slugs your allowlist parses,
like the sample's `/promo/…` and `/p/…` ([Known gaps](../../README.md#known-gaps)).

### 5.5 Validate before you route

A deep link is untrusted input even though the OS delivered it (MASTG-TEST-0028): any web page can
craft `https://link.example.sk/anything?x=y`. Route on what `DeepLinkAllowlist` returns, or not at
all:

```kotlin
val allowlist = DeepLinkAllowlist.builder()
    .host("link.example.sk")
    .pathPrefix("/promo", "/p")
    .param("promo", "utm_campaign")
    .build()

val safe: SafeDeepLink? = allowlist.parse(open.url)          // null: reject
val safePath: SafeDeepLink? = allowlist.parsePath(link.link?.deeplinkPath)
```

The sample activity also documents the "Dirty Stream" pattern: an exported activity must never open
`intent.data` as a stream or derive a file path from anything in an intent.

## 6. Events

```kotlin
Dle.trackEvent(DleEvent.conversion("purchase", value = 24.9, currency = "EUR"))
Dle.trackEvent(DleEvent.custom("signup", mapOf("plan" to "pro")))
Dle.flush()   // optional; otherwise batches go out flushDelayMillis after the last enqueue
```

Events are stamped on the device, buffered in a private file (bounded by `maxQueueSize`, oldest
dropped when full), delivered in batches of at most 100, and retried with exponential backoff and
jitter, honouring `Retry-After`. `first_open` is queued once, automatically, after the first
resolve. `trackEvent` never throws.

Keep `properties` free of personal data: the engine stores them as sent, and there is no deletion
endpoint keyed on `install_id` — removing them afterwards is an SQL job for the operator
([Known gaps](../../README.md#known-gaps)). `Dle.installId` is the value such a deletion is keyed on.

## 7. Privacy statement

What the SDK does **not** do, and is tested not to do:

- **No clipboard.** It never reads the clipboard, on any Android version, for any strategy.
- **No advertising identifier**, no `READ_PHONE_STATE`, no hardware identifier. The only
  identifier is a random UUID minted on first use, stored privately and discarded with the app.
- **No fingerprinting by default.** Device signals (`language`, `screen`, `tz_offset`,
  `device_model`) are coarse values millions of devices share, are sent only when the operator
  enabled `probabilisticSignals` **and** the user granted attribution consent, and are omitted from
  the request entirely otherwise.
- **Nothing on a custom scheme.** Tokens, codes and personal data never travel on a
  custom-scheme URL.
- **Nothing sensitive in logs.** The SDK key, `Authorization` values, full URLs with their query
  string, raw install ids and raw Install Referrers never reach a log line.

What it sends: the install id, platform and versions, the raw Install Referrer (clipped to 1024
characters), the evidence you pass in, the recorded consent, App Link opens reduced to scheme, host
and path, and the events you track.

## 8. Sample

`sample/` is a one-screen application: `Application.onCreate` initialises the SDK, `MainActivity`
resolves, shows the deferred link, validates incoming links through the allowlist and lets you
record consent and track a conversion. Point it at a local `dle-control`:

```sh
gradle :sample:installDebug -Pdle.sample.endpoint=http://10.0.2.2:8081 -Pdle.sample.sdkKey=dle_pk_…
adb shell am start -a android.intent.action.VIEW -d "https://link.example.sk/promo/autumn?promo=AUTUMN20" sk.magors.dle.sample
```

## 9. Building and testing

With Gradle 8.11.1 installed locally (there is no `gradlew`), from `sdk/android`:

```sh
gradle :dle-sdk:test              # JUnit 5 + MockWebServer, JVM only, no emulator
gradle :dle-sdk:assembleRelease
gradle :dle-sdk:publishToMavenLocal
```

The unit tests assert the SDK's request and response bodies byte for byte against the literals of
`tests/Dle.ContractTests/Wire/SdkWireContractTests.cs`, and the claim code and Install Referrer
parsers against the server's own rules, so a wire change on either side fails a test before it
fails an integration.
