import type { Channel, DevicePlatform, PlatformInfo } from './types.js';

/**
 * User-agent classification.
 *
 * The channel names produced here are byte-identical to `Dle.Domain.Routing.ChannelNames`
 * on the server, so a channel observed by the SDK and a channel observed by the edge are
 * the same value in the clickstream and in routing rules.
 *
 * This is deliberately **coarse**: a small ordered table of substring probes, no version
 * matrices, no entropy accumulation. UA parsing for channel routing is not fingerprinting;
 * the SDK never combines these signals into a stable identifier (FR-227).
 */

/** Every canonical channel name, in `ChannelNames.cs` order. `app` is never produced here. */
export const CHANNELS: readonly Channel[] = [
  'browser',
  'crawler',
  'in_app_fb',
  'in_app_ig',
  'in_app_tiktok',
  'in_app_linkedin',
  'in_app_snapchat',
  'in_app_x',
  'in_app_whatsapp',
  'in_app_telegram',
  'in_app_pinterest',
  'in_app_other',
  'app',
];

/**
 * Social crawlers and search bots. They must be recognised first: several of them carry
 * the vendor name that also identifies the corresponding in-app browser (`TelegramBot` vs
 * `Telegram-Android`, `LinkedInBot` vs `LinkedInApp`).
 */
const CRAWLER_PATTERNS: readonly RegExp[] = [
  /facebookexternalhit|facebookcatalog/i,
  /\b(?:Twitterbot|Slackbot|LinkedInBot|Discordbot|TelegramBot|Pinterestbot|redditbot|Applebot|Googlebot|bingbot|DuckDuckBot|SkypeUriPreview|HeadlessChrome|python-requests|curl|wget|axios|Go-http-client|bot|crawler|spider|crawling)\b/i,
  // WhatsApp's preview fetcher identifies itself with nothing but its own token, while the
  // in-app browser carries a full Mozilla/WebKit prefix. Anchoring at the start is what
  // keeps the two apart.
  /^WhatsApp\/\d/i,
];

/**
 * Ordered in-app browser probes. First match wins, so vendor-specific markers precede the
 * generic WebView heuristics.
 */
const IN_APP_PATTERNS: readonly (readonly [Channel, RegExp])[] = [
  // FBAN/FBAV are the iOS markers, FB_IAB/FB4A the Android ones. Instagram's WebView also
  // ships FBAN on some builds, so Instagram is probed first.
  ['in_app_ig', /\bInstagram\b/i],
  ['in_app_fb', /\b(?:FBAN|FBAV|FB_IAB|FB4A|FBIOS|FBDV)\b/i],
  // `musical_ly_34.5.0` and `trill_2023…` have no word boundary after the marker.
  ['in_app_tiktok', /BytedanceWebview|musical_ly|\btrill_|\bTikTok\b|\baweme\b/i],
  ['in_app_linkedin', /\bLinkedIn(?:App)?\b/i],
  ['in_app_snapchat', /\bSnapchat\b/i],
  ['in_app_x', /\bTwitter for (?:iPhone|iPad|Android)\b|\bTwitter(?:Android|IOS)\b/i],
  ['in_app_whatsapp', /\bWhatsApp\b/i],
  ['in_app_telegram', /\bTelegram(?:-(?:Android|iOS))?\b/i],
  ['in_app_pinterest', /\bPinterest\b/i],
  // Other well-known embedded browsers that behave like the ones above.
  ['in_app_other', /\bLine\/\d|\bMicroMessenger\b|\bKAKAOTALK\b|\bViber\b|\bYahoo(?:Mobile)?App\b|\bGSA\/\d/i],
];

/** Android WebView marks itself with `; wv)` inside the platform token. */
const ANDROID_WEBVIEW = /;\s*wv\)/i;

const APPLE_MOBILE = /\b(?:iPhone|iPad|iPod)\b/i;

/**
 * On iOS, `WKWebView` reports `Mobile/<build>` but omits the `Safari/` token that mobile
 * Safari always sends. That absence is the standard, documented way to tell an embedded
 * browser from Safari itself. Third-party browsers on iOS keep the Safari token.
 */
function isIosEmbeddedBrowser(ua: string): boolean {
  return /\bMobile\/\w+/i.test(ua) && !/\bSafari\//i.test(ua) && !/\b(?:CriOS|FxiOS|EdgiOS|OPiOS)\b/i.test(ua);
}

/** Optional client hints that disambiguate iPadOS, which reports itself as a Mac. */
export interface PlatformHints {
  /** `navigator.maxTouchPoints`. An iPad on iPadOS 13+ reports a Mac UA but has touch. */
  readonly maxTouchPoints?: number;
  /** `navigator.platform`, when available. */
  readonly platform?: string;
}

interface NavigatorView {
  userAgent?: string;
  maxTouchPoints?: number;
  platform?: string;
}

function currentNavigator(): NavigatorView | undefined {
  return (globalThis as { navigator?: NavigatorView }).navigator;
}

function currentHints(nav: NavigatorView): PlatformHints {
  const hints: { maxTouchPoints?: number; platform?: string } = {};
  if (typeof nav.maxTouchPoints === 'number') hints.maxTouchPoints = nav.maxTouchPoints;
  if (typeof nav.platform === 'string') hints.platform = nav.platform;
  return hints;
}

/** Classify the operating system family. */
export function detectPlatform(userAgent: string, hints: PlatformHints = {}): DevicePlatform {
  const ua = userAgent;
  if (ua === '') return 'other';

  if (APPLE_MOBILE.test(ua)) return 'ios';
  // Android must be checked before the generic "Linux" desktop signal.
  if (/\bAndroid\b/i.test(ua)) return 'android';

  const looksLikeMac = /\bMacintosh\b/i.test(ua) || hints.platform === 'MacIntel';
  if (looksLikeMac && (hints.maxTouchPoints ?? 0) > 1) {
    // iPadOS 13+ in desktop mode: Mac user agent, touch screen, still an iPad.
    return 'ios';
  }
  if (looksLikeMac || /\b(?:Windows NT|Win64|CrOS|X11)\b/i.test(ua)) return 'desktop';

  return 'other';
}

/** Classify the browser channel. */
export function detectChannel(userAgent: string): Channel {
  const ua = userAgent;
  if (ua === '') return 'browser';

  for (const pattern of CRAWLER_PATTERNS) {
    if (pattern.test(ua)) return 'crawler';
  }
  for (const [channel, pattern] of IN_APP_PATTERNS) {
    if (pattern.test(ua)) return channel;
  }
  if (ANDROID_WEBVIEW.test(ua)) return 'in_app_other';
  if (APPLE_MOBILE.test(ua) && isIosEmbeddedBrowser(ua)) return 'in_app_other';

  return 'browser';
}

/** Extract a coarse mobile OS version, e.g. `17.5` or `14`. Desktop versions are not reported. */
export function detectOsVersion(userAgent: string): string | undefined {
  const ios = /\b(?:iPhone|iPad|iPod)\b.*?\bOS (\d+)[._](\d+)/i.exec(userAgent);
  if (ios !== null) {
    const major = ios[1];
    const minor = ios[2];
    if (major !== undefined && minor !== undefined) return `${major}.${minor}`;
  }
  const android = /\bAndroid (\d+)(?:\.(\d+))?/i.exec(userAgent);
  if (android !== null) {
    const major = android[1];
    const minor = android[2];
    if (major !== undefined) return minor === undefined ? major : `${major}.${minor}`;
  }
  return undefined;
}

/** True for every `in_app_*` channel. */
export function isInAppChannel(channel: Channel): boolean {
  return channel.startsWith('in_app_');
}

/**
 * Whether opening the native app requires a **real user tap on an anchor element**.
 *
 * Spec §A.2.6: an in-app WebView (Facebook, Instagram, TikTok, …) does not run OS-level
 * Universal Link / App Link interception when a page loads, and on iOS a
 * `window.location.href` assignment from JavaScript does not count as a user gesture. Only
 * a genuine tap on an `<a href="https://…">` can hand the navigation to the operating
 * system. This is why the smart banner renders an anchor and never a scripted redirect,
 * and why an automatic redirect out of a WebView cannot be made to work reliably.
 */
export function requiresUserTapForAppLink(info: Pick<PlatformInfo, 'channel' | 'platform'>): boolean {
  return isInAppChannel(info.channel) && (info.platform === 'ios' || info.platform === 'android');
}

/** Classify the current browser, or a supplied user-agent string. */
export function detect(userAgent?: string, hints?: PlatformHints): PlatformInfo {
  const nav = userAgent === undefined ? currentNavigator() : undefined;
  const ua = userAgent ?? (typeof nav?.userAgent === 'string' ? nav.userAgent : '');
  const resolvedHints = hints ?? (nav === undefined ? {} : currentHints(nav));
  const channel = detectChannel(ua);
  const osVersion = detectOsVersion(ua);
  const info: {
    platform: DevicePlatform;
    channel: Channel;
    isInAppWebView: boolean;
    isCrawler: boolean;
    osVersion?: string;
  } = {
    platform: detectPlatform(ua, resolvedHints),
    channel,
    isInAppWebView: isInAppChannel(channel),
    isCrawler: channel === 'crawler',
  };
  if (osVersion !== undefined) info.osVersion = osVersion;
  return info;
}
