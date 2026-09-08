/**
 * Public type surface of `@magors/dle-web`.
 *
 * The wire format of the DLE HTTP API is snake_case (`Dle.Domain.Contracts.SdkContracts`). The
 * public TypeScript surface is camelCase. The translation between the two happens in `api.ts`
 * and nowhere else, so there is exactly one place to look when the contract changes.
 */

/**
 * How an attribution was established. Mirrors `ResolveResponseDto.match_type`.
 *
 * `probabilistic` is NEVER presented as certain: it is opt-in, consent-gated, and always
 * carried together with a `confidence` below 1.0 (spec §0.2, §A.2.5, §E.6).
 */
export type MatchType =
  | 'none'
  | 'install_referrer'
  | 'login'
  | 'claim_code'
  | 'probabilistic'
  | 'direct_open';

/** Device platform reported to `/v1/resolve`. Mirrors `Dle.Domain.Clients.Platform`. */
export type DevicePlatform = 'ios' | 'android' | 'desktop' | 'other';

/**
 * Canonical channel names. Byte-for-byte identical to `Dle.Domain.Routing.ChannelNames`
 * so that a channel detected on the client and a channel detected on the edge are the
 * same string in the clickstream. `app` is what a native SDK reports; a browser never
 * classifies itself as `app`, but the union mirrors the server so the two never drift.
 */
export type Channel =
  | 'browser'
  | 'in_app_fb'
  | 'in_app_ig'
  | 'in_app_tiktok'
  | 'in_app_linkedin'
  | 'in_app_snapchat'
  | 'in_app_x'
  | 'in_app_whatsapp'
  | 'in_app_telegram'
  | 'in_app_pinterest'
  | 'in_app_other'
  | 'crawler'
  | 'app';

/** Event kinds accepted by `POST /v1/events` (`SdkEventNames`, spec §B.7.2). */
export type DleEventType = 'link_open' | 'first_open' | 'session' | 'conversion' | 'custom';

/** Supported UI languages. `auto` derives from `navigator.language` (NFR-15: EN + SK). */
export type Language = 'en' | 'sk' | 'auto';

/** Console verbosity. `silent` disables all SDK output. */
export type LogLevel = 'silent' | 'error' | 'warn' | 'info' | 'debug';

/** A string that is either language-neutral or explicitly localised to EN and SK. */
export type LocalizedText = string | { readonly en: string; readonly sk: string };

/**
 * Consent signal handed to the engine. Both flags default to `false` — the SDK is
 * consent-first, so nothing is collected until the host site's CMP says otherwise
 * (ePrivacy art. 5(3), spec §E.6.2).
 */
export interface DleConsent {
  /** Permission to report behavioural events (`POST /v1/events`). */
  readonly analytics: boolean;
  /** Permission to link a click to this browser (persistent id, signals, link parameters). */
  readonly attribution: boolean;
  /** ISO-8601 timestamp of the moment consent was captured. Set automatically. */
  readonly ts?: string;
}

/** A single event queued for `POST /v1/events`. */
export interface DleEvent {
  readonly type: DleEventType;
  /** Free-form event name, required in practice for `conversion` and `custom`. */
  readonly name?: string;
  /** URL the event relates to. */
  readonly url?: string;
  /** Monetary value for `conversion`. */
  readonly value?: number;
  /** ISO-4217 currency code, e.g. `EUR`. */
  readonly currency?: string;
  /** ISO-8601 timestamp. Filled in at enqueue time when omitted. */
  readonly ts?: string;
  /** Flat string properties. Keep them free of personal data. */
  readonly properties?: Readonly<Record<string, string>>;
}

/** The link an attribution resolved to. */
export interface ResolveLink {
  readonly id: string;
  readonly deeplinkPath?: string;
  readonly campaign?: string;
  readonly title?: string;
}

/**
 * Result of `POST /v1/resolve`.
 *
 * `matchType` and `confidence` are always present together. A `probabilistic` match with
 * `confidence` 0.6 is a hint, not a fact — surface it as such in the host application
 * (see `isDeterministic`).
 */
export interface ResolveResult {
  readonly matched: boolean;
  readonly matchType: MatchType;
  /** 0.0 – 1.0. Deterministic matches are 1.0; probabilistic matches never are. */
  readonly confidence: number;
  readonly clickId?: string;
  readonly link?: ResolveLink;
  /** Link parameters (utm_*, custom payload). Always a plain string map. */
  readonly params: Readonly<Record<string, string>>;
  /** Seconds until this result stops being valid. `0` means "final, do not re-request". */
  readonly expiresIn: number;
}

/** Per-call overrides for {@link DleClient.resolve}. */
export interface ResolveOptions {
  /** One-time code the user pasted or typed. Never transported over a custom URI scheme. */
  readonly claimCode?: string;
  /** Deterministic login-based match key (a pseudonymous, app-supplied identifier). */
  readonly loginKey?: string;
  /**
   * Explicit referrer string in Install-Referrer form (`dl_cid=…&utm_source=…`, percent
   * encoded). Overrides the value derived from `location.search`. The SDK never reads
   * `document.referrer`.
   */
  readonly referrer?: string;
  /** Skip the per-tab cache and hit the network again. Use sparingly: 5 calls/hour. */
  readonly force?: boolean;
}

/** Strings rendered by the smart banner. Supply overrides to match your product voice. */
export interface SmartBannerStrings {
  /** Accessible name of the banner landmark. */
  readonly regionLabel: string;
  /** Secondary line under the app name. */
  readonly tagline: string;
  /** Label of the call-to-action anchor. */
  readonly open: string;
  /** Accessible name of the dismiss button. */
  readonly dismiss: string;
  /** Shown only inside in-app browsers, where only a real tap opens the app (spec §A.2.6). */
  readonly inAppHint: string;
}

/** Smart app banner configuration (FR-224). */
export interface SmartBannerOptions {
  /** Set to `false` to skip the banner entirely. Default `true`. */
  readonly enabled?: boolean;
  /** App name shown as the banner headline. */
  readonly appName: string;
  /**
   * Destination of the call-to-action. MUST be an `http(s)` URL — normally the DLE short
   * link. A custom URI scheme is rejected: never transport codes or tokens over one
   * (spec §A.2.3 / §E.7).
   */
  readonly openUrl: string;
  /** Optional app icon. Only `https:` and `data:image/*` sources are accepted. */
  readonly iconUrl?: string;
  /** Secondary line. Provide `{ en, sk }` to localise. */
  readonly tagline?: LocalizedText;
  /** Where the banner sticks. Default `top`. */
  readonly position?: 'top' | 'bottom';
  /** Platforms the banner is shown on. Default `['ios', 'android']`. */
  readonly showOn?: readonly DevicePlatform[];
  /** How long a dismissal is remembered, in days. Default 7. */
  readonly dismissForDays?: number;
  /** Force a language. Default: inherit from {@link DleConfig.language}. */
  readonly language?: Language;
  /** Force a colour scheme. Default `auto` (follows `prefers-color-scheme`). */
  readonly theme?: 'auto' | 'light' | 'dark';
  /** Stacking context. Default 2147483000 (below the browser UI, above typical page chrome). */
  readonly zIndex?: number;
  /** Per-string overrides, merged over the built-in EN/SK strings. */
  readonly strings?: Partial<SmartBannerStrings>;
  /** Called after the user activates the call-to-action. */
  readonly onOpen?: (url: string) => void;
  /** Called after the banner is dismissed, with how the dismissal happened. */
  readonly onDismiss?: (reason: DismissReason) => void;
}

/** How a smart banner was dismissed. */
export type DismissReason = 'button' | 'escape' | 'api';

/** Handle returned by {@link DleClient.showSmartBanner}. */
export interface SmartBannerHandle {
  /** The element hosting the shadow root. */
  readonly host: HTMLElement;
  /** The banner's shadow root (open, so host pages and tests can inspect it). */
  readonly shadowRoot: ShadowRoot;
  /** Dismiss and remember the dismissal. */
  dismiss(reason?: DismissReason): void;
  /** Remove the banner from the DOM without recording a dismissal. */
  destroy(): void;
}

/** SDK configuration passed to `createDle`. */
export interface DleConfig {
  /** Base URL of the DLE SDK plane (`dle-control`, default port 8081). */
  readonly endpoint: string;
  /**
   * Publishable SDK key, sent as `Authorization: Bearer <sdkKey>`. It is embedded in page
   * source by design and is not a secret; server-side authorisation is what protects data.
   */
  readonly sdkKey: string;
  /** Initial consent state. Anything omitted is `false`. A stored decision wins over this. */
  readonly consent?: Partial<DleConsent>;
  /**
   * Opt in to sending coarse device signals (language, screen, timezone offset) so the
   * engine may attempt a probabilistic match. Off by default, and additionally gated on
   * `consent.attribution` at call time. Never a fingerprint hash (spec §0.2, FR-227).
   */
  readonly probabilisticSignals?: boolean;
  /**
   * Read allow-listed link parameters (`dl_cid`, `utm_*`) from `location.search` and pass
   * them to `/v1/resolve` as the referrer. Default `true`, still gated on
   * `consent.attribution`, and only when a `dl_cid` is actually present.
   */
  readonly readUrlParams?: boolean;
  /** Call {@link DleClient.resolve} once during `createDle`. Default `false`. */
  readonly autoResolve?: boolean;
  /** Smart banner configuration. Omit to skip the banner entirely. */
  readonly smartBanner?: SmartBannerOptions;
  /** Host application version, reported as `app_version`. */
  readonly appVersion?: string;
  /** Override the detected platform sent to `/v1/resolve`. */
  readonly platform?: DevicePlatform;
  /** Per-request timeout in milliseconds. Default 4000. */
  readonly timeoutMs?: number;
  /** UI language. Default `auto`. */
  readonly language?: Language;
  /** Console log level. Default `warn`. */
  readonly logLevel?: LogLevel;
  /** Maximum number of buffered events before the oldest are dropped. Default 200. */
  readonly maxQueueSize?: number;
  /**
   * Debounce before an automatic flush, in milliseconds. Default 3000. Keeps the SDK
   * inside the documented `/v1/events` budget of 60 events per minute (spec §E.9).
   */
  readonly flushIntervalMs?: number;
  /** Storage key namespace. Default `dle`. */
  readonly storageNamespace?: string;
  /** Inject a `fetch` implementation (tests, non-browser hosts). */
  readonly fetchImpl?: typeof fetch;
}

/** The object returned by `createDle`. */
export interface DleClient {
  /** SDK version string. */
  readonly version: string;
  /** Ask the engine which link brought this browser here. */
  resolve(options?: ResolveOptions): Promise<ResolveResult>;
  /** Queue an event. Dropped without a network call when analytics consent is absent. */
  track(event: DleEvent | readonly DleEvent[]): void;
  /** Flush the queue now. Resolves to `true` when the queue is empty afterwards. */
  flush(): Promise<boolean>;
  /** Replace part of the consent state and apply the gates immediately. */
  setConsent(consent: Partial<DleConsent>): DleConsent;
  /** Read the current consent state. */
  getConsent(): DleConsent;
  /** The install id, or `null` when there is none (no attribution consent, no id). */
  getInstallId(): string | null;
  /** Detected platform and channel for this browser. */
  getPlatform(): PlatformInfo;
  /** Render the smart banner. Returns `null` when it is suppressed. */
  showSmartBanner(options?: SmartBannerOptions): SmartBannerHandle | null;
  /** Remove the banner without recording a dismissal. */
  hideSmartBanner(): void;
  /** Detach every listener and timer. The client is unusable afterwards. */
  destroy(): void;
}

/** Result of user-agent classification. */
export interface PlatformInfo {
  readonly platform: DevicePlatform;
  readonly channel: Channel;
  /** True for every `in_app_*` channel. */
  readonly isInAppWebView: boolean;
  /** True for social crawlers and search bots. */
  readonly isCrawler: boolean;
  /** Coarse OS version, e.g. `17.5` or `14`. Absent when the UA does not state one. */
  readonly osVersion?: string;
}
