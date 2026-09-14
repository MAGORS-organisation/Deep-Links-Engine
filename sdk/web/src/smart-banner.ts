import type { Logger } from './log.js';
import type { KeyValueStore } from './storage.js';
import type {
  DevicePlatform,
  DismissReason,
  Language,
  PlatformInfo,
  SmartBannerHandle,
  SmartBannerOptions,
  SmartBannerStrings,
} from './types.js';

/**
 * Smart app banner (FR-224), rendered into a shadow root so host CSS cannot break it and
 * the banner cannot leak styles into the host page.
 *
 * Accessibility (WCAG 2.2 AA, NFR-16): a labelled region landmark, a real `<a>` for the
 * call-to-action and a real `<button>` for dismissal, 44 px targets, AA contrast in both
 * colour schemes, a visible focus ring, `Escape` to dismiss, `lang` on the banner, no focus
 * stealing on appearance and no focus loss on dismissal. Motion only under
 * `prefers-reduced-motion: no-preference`; colours follow `prefers-color-scheme`.
 *
 * Why an anchor and not a redirect: inside an in-app WebView (Facebook, Instagram, TikTok,
 * …) the operating system hands a Universal Link / App Link to the native app **only on a
 * genuine user tap on an `<a>` element**. `window.location.href` from script is not a user
 * gesture on iOS (spec §A.2.6). The banner's anchor IS that tap; nothing here ever calls
 * `location.assign`, opens a custom URI scheme or fires a timer-driven redirect.
 */

/** Storage key holding the epoch millisecond until which the banner stays dismissed. */
export const DISMISSED_UNTIL_KEY = 'banner.dismissed_until';

type Lang = 'en' | 'sk';

/** Built-in strings. Language-neutral product copy is the integrator's to override. */
export const BANNER_STRINGS: Readonly<Record<Lang, SmartBannerStrings>> = {
  en: {
    regionLabel: 'App banner',
    tagline: 'Faster in the app',
    open: 'Open',
    dismiss: 'Dismiss banner',
    inAppHint: 'Tap Open to continue in the app.',
  },
  sk: {
    regionLabel: 'Banner aplikácie',
    tagline: 'V aplikácii je to rýchlejšie',
    open: 'Otvoriť',
    dismiss: 'Zavrieť banner',
    inAppHint: 'Ťuknite na Otvoriť a pokračujte v aplikácii.',
  },
};

/** Pick a UI language: an explicit choice wins, `auto` follows the browser (SK, else EN). */
export function resolveLanguage(preference: Language, navigatorLanguage?: string): Lang {
  if (preference === 'en' || preference === 'sk') return preference;
  const nav = navigatorLanguage ?? (globalThis as { navigator?: { language?: string } }).navigator?.language;
  return typeof nav === 'string' && /^sk\b/i.test(nav) ? 'sk' : 'en';
}

/** `true` while a previous dismissal is still remembered. */
export function isBannerDismissed(store: KeyValueStore, now: number = Date.now()): boolean {
  const raw = store.get(DISMISSED_UNTIL_KEY);
  if (raw === null) return false;
  const until = Number(raw);
  if (!Number.isFinite(until) || until <= now) {
    store.remove(DISMISSED_UNTIL_KEY);
    return false;
  }
  return true;
}

// Dark palette, applied by explicit choice or by the OS preference.
const DARK = '--bg:#1e1e1e;--fg:#f2f2f2;--fg2:#c8c8c8;--cta:#a8c7fa;--cta-fg:#062e6f';

const STYLE = `
:host{all:initial;position:fixed;left:0;right:0;display:block}
:host([data-pos=top]){top:0}
:host([data-pos=bottom]){bottom:0}
.b{--bg:#fff;--fg:#1a1a1a;--fg2:#4a4a4a;--cta:#0b57d0;--cta-fg:#fff;--from:-100%;
box-sizing:border-box;display:flex;align-items:center;gap:12px;padding:10px 12px 10px 16px;
font:500 15px/1.35 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;background:var(--bg);color:var(--fg);
box-shadow:0 1px 4px rgba(0,0,0,.25)}
.b[data-pos=top]{padding-top:calc(10px + env(safe-area-inset-top))}
.b[data-pos=bottom]{--from:100%;padding-bottom:calc(10px + env(safe-area-inset-bottom))}
.dark{${DARK}}
@media (prefers-color-scheme:dark){.auto{${DARK}}}
.i{width:44px;height:44px;border-radius:10px;flex:none}
.t{flex:1;min-width:0}
.n{display:block;font-weight:700;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.s,.h{display:block;font-size:13px;color:var(--fg2)}
.cta{flex:none;display:inline-flex;align-items:center;justify-content:center;min-width:64px;min-height:44px;
padding:0 16px;border-radius:22px;background:var(--cta);color:var(--cta-fg);text-decoration:none;font-weight:700}
.x{flex:none;width:44px;height:44px;border:0;background:none;color:var(--fg2);font:inherit;font-size:22px;
line-height:1;cursor:pointer;border-radius:50%}
.cta:focus-visible,.x:focus-visible{outline:3px solid var(--cta);outline-offset:2px}
@media (prefers-reduced-motion:no-preference){.b{animation:dle-in .25s ease-out}
@keyframes dle-in{from{transform:translateY(var(--from))}to{transform:none}}}
`;

export interface BannerContext {
  readonly store: KeyValueStore;
  readonly platform: PlatformInfo;
  readonly language: Language;
  readonly logger?: Logger | undefined;
  readonly document?: Document | undefined;
  readonly now?: (() => number) | undefined;
}

const DEFAULT_SHOW_ON: readonly DevicePlatform[] = ['ios', 'android'];

/** Build and mount the banner. Returns `null` when it must not be shown. */
export function createSmartBanner(options: SmartBannerOptions, context: BannerContext): SmartBannerHandle | null {
  const { store, platform, logger } = context;
  const doc = context.document ?? (globalThis as { document?: Document }).document;
  const now = context.now ?? ((): number => Date.now());
  const parent = (doc as { body?: HTMLElement | null } | undefined)?.body;

  if (options.enabled === false || doc === undefined || parent === null || parent === undefined) return null;
  if (!/^https?:\/\/\S+$/i.test(options.openUrl)) {
    logger?.error('smart banner: openUrl must be an http(s) URL; custom schemes are refused');
    return null;
  }
  if (options.appName.trim() === '') {
    logger?.error('smart banner: appName is required');
    return null;
  }
  if (platform.isCrawler || !(options.showOn ?? DEFAULT_SHOW_ON).includes(platform.platform)) return null;
  if (isBannerDismissed(store, now())) return null;

  const el = <K extends keyof HTMLElementTagNameMap>(tag: K, className: string, text?: string): HTMLElementTagNameMap[K] => {
    const node = doc.createElement(tag);
    node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  };

  const host = el('div', '');
  if (typeof (host as { attachShadow?: unknown }).attachShadow !== 'function') {
    logger?.warn('smart banner: shadow DOM is not supported here');
    return null;
  }

  const lang = resolveLanguage(options.language ?? context.language);
  const strings: SmartBannerStrings = { ...BANNER_STRINGS[lang], ...options.strings };
  const position = options.position ?? 'bottom';
  const tagline = options.tagline;

  host.setAttribute('data-dle-banner', '');
  host.setAttribute('data-pos', position);
  host.style.zIndex = String(options.zIndex ?? 2147483000);
  const root = host.attachShadow({ mode: 'open' });

  const banner = el('div', `b ${options.theme === 'dark' ? 'dark' : options.theme === 'light' ? 'light' : 'auto'}`);
  banner.setAttribute('data-pos', position);
  banner.setAttribute('role', 'region');
  banner.setAttribute('aria-label', strings.regionLabel);
  banner.setAttribute('lang', lang);

  const icon = options.iconUrl;
  if (icon !== undefined && (/^https:\/\/\S+$/i.test(icon) || /^data:image\/[\w.+-]+[;,]/i.test(icon))) {
    const img = el('img', 'i');
    img.src = icon;
    img.alt = '';
    img.setAttribute('aria-hidden', 'true');
    img.width = img.height = 44;
    banner.appendChild(img);
  }

  const text = el('div', 't');
  text.append(
    el('span', 'n', options.appName),
    el('span', 's', tagline === undefined ? strings.tagline : typeof tagline === 'string' ? tagline : tagline[lang]),
  );
  if (platform.isInAppWebView) text.appendChild(el('span', 'h', strings.inAppHint));

  // The genuine tap of §A.2.6. Plain anchor, same tab, no handler that cancels navigation.
  const cta = el('a', 'cta', strings.open);
  cta.href = options.openUrl;
  cta.rel = 'noopener';
  cta.setAttribute('aria-label', `${strings.open}: ${options.appName}`);

  const close = el('button', 'x', '×');
  close.type = 'button';
  close.setAttribute('aria-label', strings.dismiss);

  banner.append(text, cta, close);
  root.append(el('style', ''), banner);
  (root.firstChild as HTMLStyleElement).textContent = STYLE;

  let mounted = true;

  const unmount = (): void => {
    if (!mounted) return;
    mounted = false;
    doc.removeEventListener('keydown', onKeydown);
    // Do not strand keyboard focus inside a removed subtree (WCAG 2.4.3).
    if (doc.activeElement === host) host.blur();
    host.remove();
  };

  const dismiss = (reason: DismissReason = 'api'): void => {
    if (!mounted) return;
    const days = options.dismissForDays ?? 7;
    if (days > 0) store.set(DISMISSED_UNTIL_KEY, String(now() + days * 86400000));
    unmount();
    try {
      options.onDismiss?.(reason);
    } catch (error) {
      logger?.error('onDismiss threw', error);
    }
  };

  const onKeydown = (event: KeyboardEvent): void => {
    if (event.key === 'Escape' || event.key === 'Esc') dismiss('escape');
  };

  close.addEventListener('click', () => {
    dismiss('button');
  });
  cta.addEventListener('click', () => {
    // Observe only. Never preventDefault(): the default action is the app hand-off itself.
    try {
      options.onOpen?.(options.openUrl);
    } catch (error) {
      logger?.error('onOpen threw', error);
    }
  });
  doc.addEventListener('keydown', onKeydown);

  parent.appendChild(host);

  return { host, shadowRoot: root, dismiss, destroy: unmount };
}
