# Shared Kernel — záväzný kontrakt (`Dle.Domain`)

> **Toto je normatívny dokument.** Každý projekt v riešení kóduje proti typom uvedeným nižšie.
> Ak implementácia potrebuje typ, ktorý tu nie je, **nesmie** si ho vymyslieť v cudzom namespace —
> pridá si vlastný typ do vlastného projektu.
>
> Zdroj: `docs/zadanie.md` (kompletné zadanie, §B.5 dátový model, §B.6 toky, §C.3 vzory).

## 0. Globálne pravidlá

| Pravidlo | Hodnota |
|---|---|
| TFM | `net10.0` |
| `LangVersion` | `latest` (C# 14) |
| Nullable | `enable` |
| ImplicitUsings | `enable` |
| `TreatWarningsAsErrors` | `true` |
| `AnalysisLevel` | `latest-Recommended` |
| Package management | Central Package Management (`Directory.Packages.props`), `ManagePackageVersionsCentrally=true` |
| Serializácia | `System.Text.Json` **so source-generated kontextom**, nikdy reflexia na hot path |
| Časy | výhradne `DateTimeOffset` v UTC; nikdy `DateTime.Now`; čas sa berie z `TimeProvider` |
| ID entít | **plain `Guid` / `long` / `string`** — žiadne strongly-typed ID wrappery (zámerne, kvôli EF Core + Dapper + JSON) |
| Kultúra | všetky `ToString`/`Parse` s `CultureInfo.InvariantCulture` |

`Dle.Domain` **nesmie** referencovať ASP.NET Core, EF Core, Npgsql, Dapper ani žiadny NuGet balík
okrem `System.Text.Json` (súčasť BCL) a `Microsoft.Extensions.Logging.Abstractions`.

---

## 1. `Dle.Domain.Primitives`

```csharp
namespace Dle.Domain.Primitives;

/// Normalizácia hostiteľa: lowercase invariant, odstránený port, odstránená koncová bodka,
/// IDN → punycode, odstránený prefix "www.".
public static class HostNormalizer
{
    public static string Normalize(string host);
    public static bool TryNormalize(string? host, out string normalized);
    public const int MaxLength = 253;
}

/// Politika slugov. Generovaný tvar = presne 8 znakov base62 (ADR-007).
/// Vlastný slug = 3–2 znaky alebo ≥ 9 znakov, aby bol odlíšiteľný od generovaného.
public static class SlugPolicy
{
    public const int GeneratedLength = 8;
    public const int MinCustomLength = 3;
    public const int MaxLength = 64;
    public static readonly string[] Reserved; // ".well-known", "api", "healthz", "readyz", "abuse", "_dl", "favicon.ico", "robots.txt", "static", "assets"

    /// Normalizácia: NFKC, lowercase invariant, odmietnutie homoglyfov/nepovolených znakov (TC-109).
    public static bool TryNormalize(string? raw, out string slug);
    public static bool IsValidCustom(string slug);
    public static bool IsReserved(string slug);
    public static bool LooksGenerated(string slug); // presne 8 znakov, len [0-9A-Za-z]
}

/// Base62 kódovanie/dekódovanie 64-bitových a 47-bitových hodnôt. Abeceda: "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"
public static class Base62
{
    public const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    public static string Encode(ulong value, int minLength = 1);
    public static bool TryDecode(ReadOnlySpan<char> text, out ulong value);
}

/// Porovnávanie verzií typu "18", "18.1", "18.1.2", "3.4.1-beta". Chýbajúce zložky = 0.
/// Predrelease sufix sa ignoruje. Vracia záporné/0/kladné ako Comparer.
public static class VersionComparer
{
    public static int Compare(string? left, string? right);
    public static bool TryNormalize(string? raw, out string normalized);
}
```

---

## 2. `Dle.Domain.Privacy`

```csharp
namespace Dle.Domain.Privacy;

public enum ConsentMode { Off = 0, AggregateOnly = 1, Full = 2 }

/// Signál súhlasu prijatý zo SDK alebo z webu (CMP). §E.6.2
public sealed record ConsentSignal
{
    public bool Analytics { get; init; }
    public bool Attribution { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
    public string? Source { get; init; }   // "sdk" | "cmp" | "tcf" | "header"
}

/// Výsledok consent gate — je to VSTUP do rozhodovacieho pipeline, nie post-hoc filter (§B.1/5).
public sealed record ConsentDecision
{
    public required ConsentMode EffectiveMode { get; init; }
    public required bool StoreIpHash { get; init; }
    public required bool StoreIpPrefix { get; init; }
    public required bool StoreDeviceSignals { get; init; }
    public required bool AllowClickIdLinking { get; init; }
    public required bool AllowProbabilisticMatch { get; init; }
    public required string Reason { get; init; }

    public static ConsentDecision Denied(string reason);
}

public static class ConsentGate
{
    /// tenantMode = Tenant.ConsentMode; domainOverride = LinkDomain.ConsentModeOverride (môže byť null).
    /// Efektívny režim = min(tenantMode, domainOverride ?? tenantMode) — nikdy nie viac než tenant povolil.
    /// Full sa uplatní LEN ak signal?.Attribution == true (ePrivacy čl. 5(3), EDPB 2/2023).
    public static ConsentDecision Evaluate(
        ConsentMode tenantMode,
        ConsentMode? domainOverride,
        ConsentSignal? signal);
}
```

Matica správania (musí platiť; kryté testami TC-145/TC-146):

| Efektívny režim | IpHash | IpPrefix | DeviceSignals | ClickIdLinking | Probabilistic |
|---|---|---|---|---|---|
| `Off` | ❌ | ❌ | ❌ | ❌ | ❌ |
| `AggregateOnly` | ✅ | ❌ | ❌ | ❌ | ❌ |
| `Full` **a** `signal.Attribution == true` | ✅ | ✅ | ✅ | ✅ | ✅ |
| `Full` **a** `signal == null \|\| !signal.Attribution` | ✅ | ❌ | ❌ | ❌ | ❌ |

---

## 3. `Dle.Domain.Clients`

```csharp
namespace Dle.Domain.Clients;

public enum Platform { Unknown = 0, Ios = 1, Android = 2, Desktop = 3, Other = 4 }
public enum DeviceClass { Unknown = 0, Phone = 1, Tablet = 2, Desktop = 3, Bot = 4 }

public enum ClientChannel
{
    Unknown = 0, Browser = 1, Crawler = 2,
    InAppFacebook = 10, InAppInstagram = 11, InAppTikTok = 12, InAppLinkedIn = 13,
    InAppSnapchat = 14, InAppTwitter = 15, InAppWhatsApp = 16, InAppTelegram = 17,
    InAppPinterest = 18, InAppGeneric = 19,
    NativeApp = 30
}

/// Transportne neutrálny popis prichádzajúcej požiadavky. Edge mapuje HttpContext → ClientRequest.
/// Dôvod: Dle.Domain nesmie závisieť od ASP.NET Core.
public sealed record ClientRequest
{
    public required string Host { get; init; }
    public required string Path { get; init; }
    public string? UserAgent { get; init; }
    public string? AcceptLanguage { get; init; }
    public string? Referrer { get; init; }
    public System.Net.IPAddress? RemoteIp { get; init; }
    public string? SecFetchSite { get; init; }
    public string? SecFetchMode { get; init; }
    public string? SecFetchDest { get; init; }
    public IReadOnlyDictionary<string, string> Query { get; init; } = ReadOnlyDictionary<string,string>.Empty;
    public IReadOnlyDictionary<string, string> Headers { get; init; } = ReadOnlyDictionary<string,string>.Empty;
    public required DateTimeOffset ReceivedAt { get; init; }
}

public sealed record GeoLocation
{
    public string? Country { get; init; }   // ISO-3166-1 alpha-2, UPPERCASE
    public string? Region { get; init; }
    public string? City { get; init; }
}

/// Klasifikovaný klient — vstup do routovania aj do klikstreamu.
public sealed record ClientContext
{
    public required Platform Platform { get; init; }
    public required DeviceClass DeviceClass { get; init; }
    public required ClientChannel Channel { get; init; }
    public string? UaFamily { get; init; }
    public string? OsFamily { get; init; }
    public string? OsVersion { get; init; }      // normalizované cez VersionComparer.TryNormalize
    public string? AppVersion { get; init; }
    public string? Country { get; init; }
    public string? Region { get; init; }
    public string? Language { get; init; }       // primárny podznak z Accept-Language, lowercase ("sk")
    public string? ReferrerHost { get; init; }
    public bool IsCrawler { get; init; }
    public bool IsSpoofedBot { get; init; }      // TC-107: UA tvrdí bot, reverse DNS nesedí
    public string? CrawlerName { get; init; }
    public System.Net.IPAddress? RemoteIp { get; init; }
    public IReadOnlyDictionary<string, string> Query { get; init; } = ReadOnlyDictionary<string,string>.Empty;
    public required DateTimeOffset ReceivedAt { get; init; }

    public static ClientContext Empty { get; }
}
```

---

## 4. `Dle.Domain.Routing`

```csharp
namespace Dle.Domain.Routing;

public enum RoutingActionKind { Web = 0, AppOrStore = 1, StoreOnly = 2, AppOnly = 3, Block = 4 }
public enum InterstitialMode { Auto = 0, Always = 1, Never = 2 }

/// Výsledná trieda odpovede, ktorú Edge premení na HTTP odpoveď (ADR-009).
public enum DecisionKind { Web = 0, Store = 1, AppDirect = 2, Interstitial = 3, Blocked = 4, NotFound = 5, Gone = 6, Preview = 7 }

public sealed record VersionPredicate
{
    public string? Eq { get; init; }
    public string? Gt { get; init; }
    public string? Gte { get; init; }
    public string? Lt { get; init; }
    public string? Lte { get; init; }
    public bool Matches(string? actual);   // null actual → false, okrem prípadu bez predikátov
}

public sealed record TimeWindowPredicate
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public int[]? HoursUtc { get; init; }        // 0..23
    public int[]? DaysOfWeekUtc { get; init; }   // 0 = nedeľa … 6 = sobota
    public bool Matches(DateTimeOffset now);
}

public sealed record AbVariant
{
    public required string Variant { get; init; }
    public required int Percent { get; init; }   // 1..100, súčet variantov ≤ 100
}

public sealed record RuleCondition
{
    public string[]? Platform { get; init; }      // "ios"|"android"|"desktop"|"other"|"unknown"
    public VersionPredicate? OsVersion { get; init; }
    public VersionPredicate? AppVersion { get; init; }
    public string[]? Country { get; init; }       // ISO alpha-2 UPPERCASE
    public string[]? Region { get; init; }
    public string[]? Language { get; init; }      // lowercase
    public string[]? Channel { get; init; }       // "browser"|"in_app_fb"|… (viď ChannelNames)
    public TimeWindowPredicate? TimeWindow { get; init; }
    public AbVariant[]? Ab { get; init; }
}

public sealed record RuleAction
{
    public required RoutingActionKind Action { get; init; }
    public string? Url { get; init; }                  // povinné pre Web
    public string? DeeplinkPath { get; init; }         // "/product/123"
    public string? StoreUrl { get; init; }             // povinné pre AppOrStore/StoreOnly
    public string? ReferrerTemplate { get; init; }     // Android; "{click_id}", "{utm_source}", …
    public InterstitialMode Interstitial { get; init; } = InterstitialMode.Auto;
}

public sealed record RoutingRule
{
    public required string Id { get; init; }
    public RuleCondition? When { get; init; }   // null ⇒ default pravidlo (musí existovať práve raz, ako posledné)
    public required RuleAction Then { get; init; }
}

public sealed record RoutingDecision
{
    public required DecisionKind Kind { get; init; }
    public required string MatchedRuleId { get; init; }
    public required RoutingActionKind Action { get; init; }
    public string? DeeplinkPath { get; init; }
    public string? WebUrl { get; init; }
    public string? StoreUrl { get; init; }
    public string? ReferrerTemplate { get; init; }
    public InterstitialMode Interstitial { get; init; }
    public string? AbVariant { get; init; }
    public short? AbBucket { get; init; }        // 0..99

    public static RoutingDecision NotFound { get; }
    public static RoutingDecision Gone { get; }
    public static RoutingDecision Blocked(string ruleId);
}

public interface IRoutingEngine
{
    /// Deterministické: prvé zhodné pravidlo vyhráva (FR-127). Ak nič nezhoduje → default pravidlo.
    /// Ak default chýba (nemalo by sa stať, validácia to nepustí) → DecisionKind.NotFound.
    /// A/B bucket = ConsistentBucket(clickId) ∈ 0..99 — deterministické podľa click ID (FR-125).
    RoutingDecision Evaluate(
        IReadOnlyList<RoutingRule> rules,
        ClientContext client,
        ConsentDecision consent,
        string clickId);
}

public sealed class RoutingEngine : IRoutingEngine { public RoutingEngine(TimeProvider timeProvider); }

/// Kanonické mená kanálov používané v JSON pravidlách aj v klikstreame.
public static class ChannelNames
{
    public const string Browser = "browser";
    public const string Crawler = "crawler";
    public const string InAppFacebook = "in_app_fb";
    public const string InAppInstagram = "in_app_ig";
    public const string InAppTikTok = "in_app_tiktok";
    public const string InAppLinkedIn = "in_app_linkedin";
    public const string InAppSnapchat = "in_app_snapchat";
    public const string InAppTwitter = "in_app_x";
    public const string InAppWhatsApp = "in_app_whatsapp";
    public const string InAppTelegram = "in_app_telegram";
    public const string InAppPinterest = "in_app_pinterest";
    public const string InAppGeneric = "in_app_other";
    public const string NativeApp = "app";
    public static string From(ClientChannel channel);
    public static ClientChannel Parse(string? name);
}

/// Deterministické bucketovanie A/B podľa click ID (bez závislosti na náhode).
public static class ConsistentBucket
{
    public static short Of(string clickId);       // FNV-1a 64 → % 100
}

public sealed record RoutingValidationError(string Path, string Message);

public static class RoutingRuleValidator
{
    public const int MaxRules = 50;
    public const int MaxJsonBytes = 64 * 1024;

    /// FR-127 / TC-105: pravidlá BEZ default pravidla sa nesmú uložiť.
    /// Kontroluje aj: unikátne Id, povinné URL podľa akcie, súčet AB percent ≤ 100,
    /// platné schémy URL (len http/https), default musí byť posledné.
    public static IReadOnlyList<RoutingValidationError> Validate(IReadOnlyList<RoutingRule>? rules);
    public static bool IsValid(IReadOnlyList<RoutingRule>? rules);
}

/// Zostavenie finálnych URL. Nikdy nepreberá cieľ z query parametrov požiadavky (TC-164).
public static class RoutingUrlBuilder
{
    /// Play/App Store URL + referrer/campaign parametre. clickId sa vkladá do referrer šablóny.
    public static string BuildStoreUrl(RoutingDecision decision, LinkSnapshot link, ClientContext client, string clickId);

    /// Web fallback + UTM + doplnené click-id parametre (FR-128).
    public static string BuildWebUrl(RoutingDecision decision, LinkSnapshot link, ClientContext client, string clickId);

    /// myapp://host/path alebo https://host/deeplink — pre <a> tlačidlo na interstitiale.
    public static string? BuildDeeplinkUrl(RoutingDecision decision, LinkSnapshot link, string? customScheme, string clickId);

    /// Parametre, ktoré sa smú preniesť z požiadavky do cieľa (allowlist, §E.6.3).
    public static readonly string[] ForwardableQueryKeys;
    // utm_source, utm_medium, utm_campaign, utm_term, utm_content, gclid, fbclid, ttclid,
    // msclkid, twclid, li_fat_id, igshid, ref, dl_cid
}
```

---

## 5. `Dle.Domain.Links`

```csharp
namespace Dle.Domain.Links;

public sealed record OgMeta
{
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? ImageUrl { get; init; }
    public string? SiteName { get; init; }
    public string? Type { get; init; }       // default "website"
    public string? TwitterCard { get; init; } // default "summary_large_image"
    public static OgMeta Empty { get; }
    public OgMeta MergeWith(OgMeta? fallback);
}

public enum LinkServeState { Servable = 0, NotFound = 1, Gone = 2, Expired = 3, NotYetActive = 4 }

/// Nemenný snapshot linku pre hot path. Serializuje sa do L2 cache (Valkey) — musí byť
/// pokrytý source-generated JSON kontextom `DleDomainJsonContext`.
public sealed record LinkSnapshot
{
    public required long Id { get; init; }
    public required Guid TenantId { get; init; }
    public required Guid DomainId { get; init; }
    public required string Slug { get; init; }
    public required string TargetUrl { get; init; }
    public string? DeeplinkPath { get; init; }
    public required IReadOnlyList<RoutingRule> RoutingRules { get; init; }
    public required OgMeta Og { get; init; }
    public required IReadOnlyDictionary<string, string> Utm { get; init; }
    public string? Title { get; init; }
    public Guid? CampaignId { get; init; }
    public required bool IsActive { get; init; }
    public DateTimeOffset? StartsAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? ExpiredUrl { get; init; }
    public DateTimeOffset? QuarantinedAt { get; init; }
    public required ConsentMode TenantConsentMode { get; init; }
    public ConsentMode? DomainConsentMode { get; init; }
    public string? IosCustomScheme { get; init; }
    public string? AndroidCustomScheme { get; init; }
    public string? IosStoreUrl { get; init; }
    public string? AndroidStoreUrl { get; init; }

    /// Poradie kontrol: quarantined → Gone; !IsActive → NotFound;
    /// now < StartsAt → NotYetActive; now ≥ ExpiresAt → Expired; inak Servable.
    public LinkServeState GetServeState(DateTimeOffset now);
    public bool IsServable(DateTimeOffset now);   // == GetServeState(now) is Servable
}
```

---

## 6. `Dle.Domain.WellKnown`

Čisté funkcie generujúce AASA a `assetlinks.json` (§C.3.3, FR-141/142). Testovateľné bez HTTP.

```csharp
namespace Dle.Domain.WellKnown;

public sealed record AasaAppEntry
{
    public required string AppId { get; init; }         // "ABCDE12345.sk.zakaznik.app"
    public string? AppClipAppId { get; init; }
    public IReadOnlyList<AasaComponent> Components { get; init; } = [];
}

public sealed record AasaComponent
{
    public string? Path { get; init; }        // JSON kľúč "/"
    public string? Fragment { get; init; }    // JSON kľúč "#"
    public IReadOnlyDictionary<string, string>? Query { get; init; }  // JSON kľúč "?"
    public bool? Exclude { get; init; }
    public string? Comment { get; init; }
    public bool? CaseSensitive { get; init; }
    public bool? PercentEncoded { get; init; }
}

public sealed record AndroidAppEntry
{
    public required string PackageName { get; init; }
    public required IReadOnlyList<string> Sha256CertFingerprints { get; init; }
    public IReadOnlyList<AasaComponent> DynamicComponents { get; init; } = [];   // Android 15+ (FR-142)
}

public sealed record WellKnownDocument(string Json, string ETag, string ContentType)
{
    public const string JsonContentType = "application/json";
}

public static class WellKnownBuilder
{
    /// Vráti null, ak pre host nie je registrovaná žiadna iOS aplikácia (TC-122: 404, NIE prázdny JSON).
    public static WellKnownDocument? BuildAasa(IReadOnlyList<AasaAppEntry> apps);

    /// Vráti null, ak pre host nie je registrovaná žiadna Android aplikácia.
    public static WellKnownDocument? BuildAssetLinks(IReadOnlyList<AndroidAppEntry> apps);

    /// Predvolené komponenty: vylúčiť /.well-known/*, /api/*, /healthz; povoliť /*; vylúčiť fragment "no_dl".
    public static IReadOnlyList<AasaComponent> DefaultComponents { get; }
}

public sealed record WellKnownValidationIssue(string Code, string Message, bool IsError);

/// FR-143/FR-144 — validácia stiahnutého well-known súboru z reálnej domény.
public static class WellKnownValidator
{
    public static IReadOnlyList<WellKnownValidationIssue> ValidateAasa(
        string? body, string? contentType, int statusCode, int redirectCount, IReadOnlyList<string> expectedAppIds);

    public static IReadOnlyList<WellKnownValidationIssue> ValidateAssetLinks(
        string? body, string? contentType, int statusCode, int redirectCount, IReadOnlyList<string> expectedFingerprints);

    /// TC-123 / FR-144: heuristika „toto vyzerá ako upload certifikát, nie Play App Signing".
    public static bool LooksLikeUploadCertificate(string fingerprint, IReadOnlyList<string> knownPlayFingerprints);

    public const string ErrRedirect = "well_known.redirect";
    public const string ErrContentType = "well_known.content_type";
    public const string ErrStatus = "well_known.status";
    public const string ErrMalformed = "well_known.malformed";
    public const string ErrAppIdMismatch = "well_known.appid_mismatch";
    public const string WarnUploadCertificate = "well_known.upload_certificate";
}
```

---

## 7. `Dle.Domain.Analytics`

```csharp
namespace Dle.Domain.Analytics;

/// Názvy hodnôt stĺpca click_events.decision (§B.5.3).
public static class DecisionNames
{
    public const string AppOpen = "app_open";
    public const string StoreIos = "store_ios";
    public const string StoreAndroid = "store_android";
    public const string Web = "web";
    public const string Interstitial = "interstitial";
    public const string Preview = "preview";
    public const string Blocked = "blocked";
    public const string NotFound = "not_found";
    public const string Gone = "gone";
    public static string From(DecisionKind kind, Platform platform);
}

public sealed record ClickEvent
{
    public required Guid Id { get; init; }               // UUIDv7
    public required DateTimeOffset OccurredAt { get; init; }
    public required Guid TenantId { get; init; }
    public required long LinkId { get; init; }
    public required string ClickId { get; init; }
    public byte[]? IpHash { get; init; }
    public string? IpPrefix { get; init; }
    public string? UaFamily { get; init; }
    public string? OsFamily { get; init; }
    public string? OsVersion { get; init; }
    public string? DeviceClass { get; init; }            // lowercase: "phone"|"tablet"|"desktop"|"bot"|"unknown"
    public string? Country { get; init; }
    public string? Region { get; init; }
    public string? Language { get; init; }
    public string? ReferrerHost { get; init; }
    public string? Channel { get; init; }                // ChannelNames.*
    public required string Decision { get; init; }       // DecisionNames.*
    public short? AbBucket { get; init; }
    public required string ConsentMode { get; init; }    // "off"|"aggregate_only"|"full"
    public required bool IsBot { get; init; }
    public bool SpoofedBot { get; init; }
    public short? LatencyMs { get; init; }
    public IReadOnlyDictionary<string, string>? Extra { get; init; }
}

public enum SdkEventType { LinkOpen = 0, FirstOpen = 1, Session = 2, Conversion = 3, Custom = 4 }

public sealed record SdkEvent
{
    public required Guid Id { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required Guid TenantId { get; init; }
    public required Guid AppId { get; init; }
    public required string InstallId { get; init; }
    public required SdkEventType Type { get; init; }
    public string? Name { get; init; }
    public string? Url { get; init; }
    public decimal? Value { get; init; }
    public string? Currency { get; init; }
    public long? LinkId { get; init; }
    public string? ClickId { get; init; }
    public IReadOnlyDictionary<string, string>? Properties { get; init; }
}

// ---- Dotazovacie modely pre reporty (FR-202) ----
public enum TimeGrain { Hour = 0, Day = 1, Week = 2, Month = 3 }

public sealed record AnalyticsQuery
{
    public required Guid TenantId { get; init; }
    public required DateTimeOffset From { get; init; }
    public required DateTimeOffset To { get; init; }
    public TimeGrain Grain { get; init; } = TimeGrain.Day;
    public long? LinkId { get; init; }
    public Guid? CampaignId { get; init; }
    public string? Country { get; init; }
    public string? Platform { get; init; }
    public bool IncludeBots { get; init; }          // default false (FR-205)
    public int Limit { get; init; } = 500;
}

public sealed record TimeSeriesPoint(DateTimeOffset Bucket, long Clicks, long Installs, long Conversions);
public sealed record BreakdownRow(string Key, long Clicks, long Installs, decimal? Value);
public sealed record FunnelSummary(long Clicks, long Installs, long Attributed, long Conversions, decimal ConversionRate);
public sealed record MatchTypeSummary(string MatchType, long Count, decimal AverageConfidence);
```

---

## 8. `Dle.Domain.Attribution`

```csharp
namespace Dle.Domain.Attribution;

public enum MatchType { None = 0, InstallReferrer = 1, Login = 2, ClaimCode = 3, Probabilistic = 4, DirectOpen = 5 }

public static class MatchTypeNames
{
    public const string None = "none";
    public const string InstallReferrer = "install_referrer";
    public const string Login = "login";
    public const string ClaimCode = "claim_code";
    public const string Probabilistic = "probabilistic";
    public const string DirectOpen = "direct_open";
    public static string From(MatchType t);
    public static MatchType Parse(string? s);
}

/// Signály zariadenia pre probabilistický match. Spracujú sa LEN pri ConsentDecision.AllowProbabilisticMatch.
public sealed record DeviceSignals
{
    public string? Language { get; init; }
    public string? Screen { get; init; }        // "1080x2400"
    public int? TimezoneOffsetMinutes { get; init; }
    public string? OsVersion { get; init; }
    public string? DeviceModel { get; init; }
    public string? IpPrefix { get; init; }
}

public sealed record ResolveRequest
{
    public required string InstallId { get; init; }
    public required Platform Platform { get; init; }
    public string? AppVersion { get; init; }
    public string? OsVersion { get; init; }
    public string? Referrer { get; init; }        // Android Install Referrer (raw)
    public string? ClaimCode { get; init; }
    public string? LoginKey { get; init; }        // hash používateľského účtu pre login reconciliation
    public DeviceSignals? Signals { get; init; }
    public ConsentSignal? Consent { get; init; }
    public required DateTimeOffset ReceivedAt { get; init; }
}

public sealed record AttributionResult
{
    public required bool Matched { get; init; }
    public required MatchType MatchType { get; init; }
    public required decimal Confidence { get; init; }    // 0.00–1.00, deterministické = 1.00
    public string? ClickId { get; init; }
    public long? LinkId { get; init; }
    public string? DeeplinkPath { get; init; }
    public string? Campaign { get; init; }
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = ReadOnlyDictionary<string,string>.Empty;
    public IReadOnlyDictionary<string, string> Evidence { get; init; } = ReadOnlyDictionary<string,string>.Empty;

    public static AttributionResult NoMatch(string reason);
}

/// Parsovanie Android Install Referrer stringu (URL-encoded query). Hľadá kľúč "dl_cid".
public static class InstallReferrerParser
{
    public const string ClickIdKey = "dl_cid";
    public const int MaxReferrerLength = 1024;
    public static IReadOnlyDictionary<string, string> Parse(string? referrer);
    public static bool TryGetClickId(string? referrer, out string clickId);
}

/// Skórovanie probabilistickej zhody (§A.2.5). Váhy sú konfigurovateľné, defaulty tu.
public static class ProbabilisticScorer
{
    /// Váhy: IP prefix 0.45, OS verzia 0.20, jazyk 0.15, timezone 0.10, screen 0.10.
    /// Časové tlmenie: confidence *= max(0, 1 - elapsedMinutes / windowMinutes).
    /// Výsledok < minConfidence ⇒ MatchType.None (TC-147: mimo okna nikdy „slabá zhoda").
    public static decimal Score(DeviceSignals candidate, DeviceSignals click, TimeSpan elapsed, TimeSpan window);
}

public static class ClaimCode
{
    public const int Length = 6;
    public const string Alphabet = "ACDEFGHJKLMNPQRTUVWXY34679";   // bez homoglyfov
    public static string Normalize(string raw);        // uppercase, odstránené medzery/pomlčky, O→0 sa NEmapuje
    public static bool IsWellFormed(string? code);
}
```

---

## 9. `Dle.Domain.Abuse`

```csharp
namespace Dle.Domain.Abuse;

public enum UrlSafetyLevel { Unknown = 0, Safe = 1, Suspicious = 2, Malicious = 3, Blocked = 4 }

public sealed record UrlSafetyVerdict
{
    public required UrlSafetyLevel Level { get; init; }
    public required string Source { get; init; }        // "syntax"|"private_ip"|"urlhaus"|"blocklist"|"manual"
    public string? Reason { get; init; }
    public DateTimeOffset? CheckedAt { get; init; }
    public static UrlSafetyVerdict Safe(string source);
    public static UrlSafetyVerdict Reject(UrlSafetyLevel level, string source, string reason);
}

/// Syntaktická a sieťová validácia cieľových URL (T-01, T-02, TC-161…164).
public static class TargetUrlPolicy
{
    public static readonly string[] AllowedSchemes;   // "http", "https"
    /// Odmieta javascript:, data:, file:, intent:, vbscript:, about:, blob:.
    public static UrlSafetyVerdict ValidateSyntax(string? url);
    /// Súkromné, loopback, link-local, CGNAT, multicast, IPv6 ULA/mapped-IPv4, metadata endpointy.
    public static bool IsForbiddenAddress(System.Net.IPAddress address);
    public static bool IsForbiddenHost(string host);   // localhost, *.local, *.internal, metadata.google.internal
    public const int MaxUrlLength = 2048;
}

public enum AbuseReportStatus { New = 0, Triaged = 1, Confirmed = 2, Rejected = 3, Resolved = 4 }
public enum AbuseReason { Phishing = 0, Malware = 1, Spam = 2, Illegal = 3, Copyright = 4, Other = 5 }
```

---

## 10. `Dle.Domain.Crypto` (abstrakcie; implementácie v `Dle.Crypto`)

```csharp
namespace Dle.Domain.Crypto;

public static class SignatureAlgorithms
{
    public const string Hs256 = "HS256";
    public const string Ed25519 = "Ed25519";
    public const string MlDsa65 = "MLDSA65";
    public const string Ed25519MlDsa65 = "Ed25519+MLDSA65";
    public const string SlhDsa128s = "SLHDSA128s";
}

public interface ISigner
{
    string AlgorithmId { get; }
    string KeyId { get; }
    int MaxSignatureSize { get; }
    byte[] Sign(ReadOnlySpan<byte> payload);
}

public interface IVerifier
{
    bool Verify(string algorithmId, string keyId, ReadOnlySpan<byte> payload, ReadOnlySpan<byte> signature);
    IReadOnlyCollection<string> AcceptedAlgorithms { get; }
}

/// Formát: dlt1.<alg>.<kid>.<base64url(payload)>.<base64url(signature)>  (§E.4.2)
public sealed record SignedToken(string Alg, string Kid, string Payload, string Signature)
{
    public const string Prefix = "dlt1";
    public override string ToString();
    public static bool TryParse(string? value, out SignedToken token);
}

public sealed record JsonWebKey
{
    public required string Kid { get; init; }
    public required string Kty { get; init; }
    public required string Alg { get; init; }
    public required string Use { get; init; }
    public string? Crv { get; init; }
    public string? X { get; init; }
    public string? PublicKey { get; init; }     // base64url, pre PQC algoritmy bez JWK registrácie
    public DateTimeOffset? NotBefore { get; init; }
    public DateTimeOffset? NotAfter { get; init; }
}

public sealed record JwksDocument(IReadOnlyList<JsonWebKey> Keys);

public interface IKeyRing
{
    ISigner CurrentSigner { get; }
    IVerifier Verifier { get; }
    JwksDocument GetJwks();
    ValueTask RotateAsync(CancellationToken ct);
}

/// Kľúčovaná Feistelova permutácia (ADR-007) — bijekcia nad 47-bitovým priestorom.
public interface IFeistelPermutation
{
    int Bits { get; }
    ulong Encrypt(ulong value);
    ulong Decrypt(ulong value);
}

/// Slug: sekvencia → permutácia → base62(8). Reverzibilné len s kľúčom.
public interface ISlugGenerator
{
    string FromSequence(long sequenceValue);
    bool TryRecoverSequence(string slug, out long sequenceValue);
}

/// Click ID nesie zašifrovanú časovú pečiatku, aby attribution service vedel orezať partície (§B.6.3).
public interface IClickIdCodec
{
    string New(DateTimeOffset occurredAt);
    /// Vráti false pri manipulácii (T-05 / TC-167).
    bool TryDecode(string clickId, out DateTimeOffset occurredAt, out long sequence);
}

/// HMAC(IP, denne rotovaný salt) — §B.5.3, FR-247.
public interface IIpHasher
{
    byte[] Hash(System.Net.IPAddress address, DateTimeOffset when);
    string? Prefix(System.Net.IPAddress address);   // /24 pre IPv4, /48 pre IPv6
}
```

---

## 11. `Dle.Domain.Ports` — porty do infraštruktúry

```csharp
namespace Dle.Domain.Ports;

public interface ILinkStore
{
    ValueTask<LinkSnapshot?> FindAsync(string host, string slug, CancellationToken ct);
}

public interface IDomainConfigStore
{
    ValueTask<WellKnownDocument?> BuildAasaAsync(string host, CancellationToken ct);
    ValueTask<WellKnownDocument?> BuildAssetLinksAsync(string host, CancellationToken ct);
    ValueTask<DomainRuntimeConfig?> GetDomainAsync(string host, CancellationToken ct);
}

public sealed record DomainRuntimeConfig
{
    public required Guid Id { get; init; }
    public required Guid TenantId { get; init; }
    public required string Host { get; init; }
    public required ConsentMode TenantConsentMode { get; init; }
    public ConsentMode? DomainConsentMode { get; init; }
    public string? InterstitialBrandJson { get; init; }
    public string? DefaultLanguage { get; init; }
    public OgMeta? DefaultOg { get; init; }
    public bool IsActive { get; init; }
}

/// Neblokujúci zápis do bounded channel (NFR-06, FR-165).
public interface IClickEventSink
{
    bool TryWrite(ClickEvent clickEvent);
    long DroppedCount { get; }
}

public interface IClickEventWriter
{
    Task WriteBatchAsync(IReadOnlyList<ClickEvent> batch, CancellationToken ct);
}

public interface ISdkEventWriter
{
    Task WriteBatchAsync(IReadOnlyList<SdkEvent> batch, CancellationToken ct);
}

/// ADR-006 — abstrakcia nad úložiskom analytiky (Postgres default, ClickHouse opt-in).
public interface IClickAnalyticsStore
{
    Task<IReadOnlyList<TimeSeriesPoint>> GetTimeSeriesAsync(AnalyticsQuery query, CancellationToken ct);
    Task<IReadOnlyList<BreakdownRow>> GetBreakdownAsync(AnalyticsQuery query, string dimension, CancellationToken ct);
    Task<FunnelSummary> GetFunnelAsync(AnalyticsQuery query, CancellationToken ct);
    Task<IReadOnlyList<MatchTypeSummary>> GetMatchTypesAsync(AnalyticsQuery query, CancellationToken ct);
}

public interface IClientClassifier
{
    ClientContext Classify(ClientRequest request);
}

public interface IGeoIpResolver
{
    GeoLocation? Resolve(System.Net.IPAddress? address);
    bool IsAvailable { get; }
}

public interface IBotVerifier
{
    /// Reverse DNS + forward-confirm pre hlavné crawlery (FR-161, TC-107).
    ValueTask<bool> IsGenuineAsync(string crawlerName, System.Net.IPAddress? address, CancellationToken ct);
}

public interface IUrlSafetyChecker
{
    ValueTask<UrlSafetyVerdict> CheckAsync(string url, CancellationToken ct);
}

public interface IClickLookup
{
    /// Vyhľadanie kliku podľa click_id s časovým orezaním partícií (§B.6.3 kritický detail).
    ValueTask<ClickRecord?> FindByClickIdAsync(string clickId, DateTimeOffset hintFrom, DateTimeOffset hintTo, CancellationToken ct);

    /// Kandidáti pre probabilistický match v okne.
    ValueTask<IReadOnlyList<ClickRecord>> FindCandidatesAsync(Guid tenantId, DateTimeOffset from, DateTimeOffset to, string? ipPrefix, string? osFamily, CancellationToken ct);
}

public sealed record ClickRecord
{
    public required Guid Id { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required Guid TenantId { get; init; }
    public required long LinkId { get; init; }
    public required string ClickId { get; init; }
    public string? IpPrefix { get; init; }
    public string? OsFamily { get; init; }
    public string? OsVersion { get; init; }
    public string? Language { get; init; }
    public string? Country { get; init; }
    public string? DeeplinkPath { get; init; }
    public IReadOnlyDictionary<string, string>? Extra { get; init; }
}

public interface ILinkCacheInvalidator
{
    ValueTask InvalidateLinkAsync(string host, string slug, CancellationToken ct);
    ValueTask InvalidateHostAsync(string host, CancellationToken ct);
    ValueTask InvalidateTenantAsync(Guid tenantId, CancellationToken ct);
}

public interface IWebhookQueue
{
    Task EnqueueAsync(Guid tenantId, string eventType, string payloadJson, CancellationToken ct);
}
```

---

## 12. `Dle.Domain.Entities` — perzistentné entity (EF Core ich mapuje)

Presné mená stĺpcov podľa §B.5.2/B.5.3 zadania (snake_case v DB, PascalCase v C#).

```csharp
namespace Dle.Domain.Entities;

public class Tenant            // id, slug, name, status, consent_mode, settings(jsonb), created_at
public class LinkDomain        // id, tenant_id, host, is_default, tls_status, aasa_status, assetlinks_status,
                               // last_verified_at, verification_log(jsonb), consent_mode_override, branding(jsonb),
                               // default_og(jsonb), is_active, created_at
public class App               // id, tenant_id, platform, bundle_id, team_id, cert_fingerprints(text[]),
                               // play_signing_fingerprints(text[]), store_id, custom_scheme, min_app_version,
                               // appclip_bundle_id, store_url, created_at
public class AppDomain         // app_id, domain_id  (many-to-many)
public class Campaign          // id, tenant_id, name, utm(jsonb), created_at
public class Link              // podľa DDL §B.5.2 + updated_at, version, description
public class LinkVersion       // id, link_id, version, snapshot(jsonb), changed_by, changed_at, change_note (FR-107)
public class ApiKey            // id, tenant_id, name, prefix, hash(bytea, Argon2id), role, scopes(text[]),
                               // last_used_at, expires_at, revoked_at, created_at
public class SdkKey            // id, tenant_id, app_id, key_prefix, hash, is_active, created_at
public class WebhookSubscription // id, tenant_id, url, secret_encrypted, event_types(text[]), is_active, created_at
public class WebhookDelivery   // id, tenant_id, subscription_id, event_type, payload(jsonb), attempt, status,
                               // next_attempt_at, last_error, response_code, created_at, delivered_at
public class Install           // id, tenant_id, app_id, install_id, first_open_at, raw_referrer, platform, app_version,
                               // login_key_hash, created_at
public class AttributionRecord // id, tenant_id, install_id(FK), click_id, link_id, match_type, confidence,
                               // matched_at, window_seconds, evidence(jsonb)
public class ClaimCodeRecord   // id, tenant_id, code_hash, click_id, link_id, expires_at, consumed_at, created_at
public class AbuseReport       // id, link_id, tenant_id, reason, details, reporter_email_hash, status,
                               // created_at, resolved_at, resolution_note
public class AuditLogEntry     // id, tenant_id, actor_id, actor_type, action, subject_type, subject_id,
                               // metadata(jsonb), occurred_at
                               // ⚠️ §E.6.3: NIKDY neobsahuje identifikátory koncových používateľov
public class SigningKeyRecord  // id, tenant_id(null = globálny), kid, algorithm, public_key, private_key_encrypted,
                               // not_before, not_after, is_current, created_at
public class DomainVerification// id, domain_id, kind (aasa|assetlinks|tls|dns), status, issues(jsonb), checked_at
public class SlugSequence      // singleton riadok / Postgres sekvencia wrapper
public class IdempotencyRecord // key, tenant_id, request_hash, response_status, response_body, created_at, expires_at
```

Enumy uložené ako `text` (nie int) — čitateľné v DB, stabilné pri migráciách.

---

## 13. `Dle.Domain.Serialization`

```csharp
namespace Dle.Domain.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(LinkSnapshot))]
[JsonSerializable(typeof(RoutingRule[]))]
[JsonSerializable(typeof(OgMeta))]
[JsonSerializable(typeof(ClickEvent))]
[JsonSerializable(typeof(SdkEvent))]
[JsonSerializable(typeof(AttributionResult))]
[JsonSerializable(typeof(ResolveRequest))]
[JsonSerializable(typeof(JwksDocument))]
[JsonSerializable(typeof(Dictionary<string,string>))]
public sealed partial class DleDomainJsonContext : JsonSerializerContext;

public static class DleJson
{
    public static JsonSerializerOptions Default { get; }   // snake_case, string enums, no null
    public static JsonSerializerOptions WellKnown { get; } // camelCase-neutral, presné kľúče "/", "#", "?"
}
```

---

## 14. Verejné HTTP kontrakty (DTO) — `Dle.Domain.Contracts`

Používajú ich `Dle.Control`, testy aj SDK. Wire formát je **snake_case**.

```csharp
namespace Dle.Domain.Contracts;

// POST /v1/resolve   (§B.7.2)
public sealed record ResolveRequestDto
{
    public required string InstallId { get; init; }
    public required string Platform { get; init; }
    public string? AppVersion { get; init; }
    public string? OsVersion { get; init; }
    public string? Referrer { get; init; }
    public string? ClaimCode { get; init; }
    public string? LoginKey { get; init; }
    public DeviceSignalsDto? Signals { get; init; }
    public ConsentDto? Consent { get; init; }
}
public sealed record DeviceSignalsDto { string? Language; string? Screen; int? TzOffset; string? DeviceModel; }
public sealed record ConsentDto { bool Analytics; bool Attribution; DateTimeOffset? Ts; }

public sealed record ResolveResponseDto
{
    public required bool Matched { get; init; }
    public required string MatchType { get; init; }
    public required decimal Confidence { get; init; }
    public string? ClickId { get; init; }
    public ResolveLinkDto? Link { get; init; }
    public IReadOnlyDictionary<string,string> Params { get; init; }
    public int ExpiresIn { get; init; }
}
public sealed record ResolveLinkDto { string Id; string? DeeplinkPath; string? Campaign; string? Title; }

// POST /v1/events
public sealed record EventBatchDto { string InstallId; string? Platform; IReadOnlyList<EventDto> Events; }
public sealed record EventDto { string Type; string? Name; string? Url; decimal? Value; string? Currency;
                                DateTimeOffset? Ts; IReadOnlyDictionary<string,string>? Properties; }

// Control plane — linky
public sealed record CreateLinkRequest { … }   // slug?, domain_id, title?, target_url, deeplink_path?,
                                               // routing_rules, og, utm, campaign_id?, tags, starts_at?, expires_at?, expired_url?
public sealed record UpdateLinkRequest { … }
public sealed record LinkResponse { … }        // + short_url, qr_url
public sealed record SimulateRequest { string? UserAgent; string? Country; string? Language; string? OsVersion; string? AppVersion; string? Channel; }
public sealed record SimulateResponse { string MatchedRuleId; string Decision; string? Url; string? DeeplinkPath; string? StoreUrl; short? AbBucket; IReadOnlyList<string> Trace; }

// Domény, aplikácie, webhooky, abuse — analogicky; podrobnosti v OpenAPI.
public sealed record ProblemCodes                 // stabilné kódy chýb v RFC 9457 `type`
{
    public const string ValidationFailed  = "https://docs.dle.dev/problems/validation-failed";
    public const string SlugTaken         = "https://docs.dle.dev/problems/slug-taken";
    public const string UnsafeTarget      = "https://docs.dle.dev/problems/unsafe-target";
    public const string RateLimited       = "https://docs.dle.dev/problems/rate-limited";
    public const string MissingDefaultRule= "https://docs.dle.dev/problems/missing-default-rule";
    public const string DomainNotVerified = "https://docs.dle.dev/problems/domain-not-verified";
}
```

---

## 15. Konvencie skladania aplikácií

Každý funkčný modul vystavuje **presne dve** rozšírenia. `Program.cs` nič iné nevolá.

```csharp
public static class <Modul>ServiceCollectionExtensions
{
    public static IServiceCollection Add<Modul>(this IServiceCollection services, IConfiguration configuration);
}

public static class <Modul>EndpointExtensions
{
    public static IEndpointRouteBuilder Map<Modul>(this IEndpointRouteBuilder app);
}
```

### `Dle.Edge/Program.cs` volá presne:
`AddDleEdgeCore`, `AddDleTelemetry`, `AddDleFastPersistence`, `AddDleCrypto`, `AddDleEdgeRateLimiting`
→ `MapResolve`, `MapWellKnown`, `MapQr`, `MapHealth`

### `Dle.Control/Program.cs` volá presne:
`AddDleControlCore`, `AddDleTelemetry`, `AddDlePersistence`, `AddDleCrypto`, `AddDleAnalytics`,
`AddDleAttribution`, `AddDleAbuse`, `AddDleWebhooks`, `AddDleIdentity`, `AddDleWorkers`, `AddDleControlRateLimiting`
→ `MapLinks`, `MapDomains`, `MapApps`, `MapTenants`, `MapApiKeys`, `MapAnalytics`, `MapAttribution`,
`MapAbuse`, `MapWebhooks`, `MapJwks`, `MapHealth`

---

## 16. Konfiguračné sekcie (`appsettings.json`, §C.4)

Koreňová sekcia `Dle`. Každá trieda `IOptions<T>` má `[ValidatableType]`/DataAnnotations a je registrovaná
s `.ValidateOnStart()`.

```
Dle:Edge:Interstitial:{Enabled,AutoRedirectMs,Branding}
Dle:Edge:BotDetection:{ReverseDnsVerify,CacheTtlMinutes}
Dle:Edge:GeoIp:{Provider,Path,AutoUpdate}
Dle:Edge:Cache:{L1Seconds,L2Minutes,NegativeSeconds}
Dle:Attribution:{Strategies[],Probabilistic:{Enabled,WindowMinutes,MinConfidence,RequireConsent}}
Dle:Attribution:ClaimCode:{Enabled,TtlMinutes}
Dle:Privacy:{ConsentMode,IpStorage,IpSaltRotationHours,Retention:{RawDays,AggregatedDays}}
Dle:Crypto:{SigningAlgorithm,HybridPqEnabled,KeyRotationDays,SlugFeistelRounds}
Dle:Analytics:{Provider}            # postgres | clickhouse
Dle:RateLimits:{...}                # §E.9, konkrétne hodnoty
Dle:Webhooks:{MaxAttempts,BaseDelaySeconds,TimeoutSeconds}
Dle:Abuse:{UrlHausEnabled,BlocklistPath,RecheckIntervalHours}
ConnectionStrings:{Postgres,Valkey,ClickHouse}
```

---

## 17. Čo je zakázané (code review gate)

1. `HTTP 301` kdekoľvek pre link (ADR-009) — vždy `permanent: false`.
2. `DateTime.Now` / `DateTime.UtcNow` — len `TimeProvider`.
3. Reflexná serializácia na hot path — len source-generated `JsonSerializerContext`.
4. Prevzatie cieľa presmerovania z query parametra požiadavky (TC-164).
5. Logovanie `Authorization`, celého referrera, celého query stringu alebo surovej IP na úrovni `Information` a nižšej (T-08, S-08).
6. Porovnávanie tajomstiev cez `==` — len `CryptographicOperations.FixedTimeEquals` (T-17, S-03).
7. `403` pri cudzom tenantovi — vždy `404` (TC-166).
8. Blokujúce I/O v resolve ceste okrem cache a jedného DB dotazu.
9. `catch { }` bez logovania a bez explicitného `default: deny` (T-16 / OWASP A10:2025).
