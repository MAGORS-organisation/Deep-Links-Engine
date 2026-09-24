import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { Logger } from '../src/log';
import { BANNER_STRINGS, DISMISSED_UNTIL_KEY, createSmartBanner, isBannerDismissed, resolveLanguage } from '../src/smart-banner';
import { createMemoryStore } from '../src/storage';
import type { KeyValueStore } from '../src/storage';
import type { PlatformInfo, SmartBannerHandle, SmartBannerOptions } from '../src/types';
import { setNavigatorLanguage } from './helpers';

const IOS_SAFARI: PlatformInfo = { platform: 'ios', channel: 'browser', isInAppWebView: false, isCrawler: false, osVersion: '17.5' };
const IOS_INSTAGRAM: PlatformInfo = { platform: 'ios', channel: 'in_app_ig', isInAppWebView: true, isCrawler: false };
const DESKTOP: PlatformInfo = { platform: 'desktop', channel: 'browser', isInAppWebView: false, isCrawler: false };
const CRAWLER: PlatformInfo = { platform: 'other', channel: 'crawler', isInAppWebView: false, isCrawler: true };

const OPEN_URL = 'https://links.example.sk/aB3xK9pQ';

function silentLogger(): { logger: Logger; errors: unknown[][] } {
  const errors: unknown[][] = [];
  const noop = (): void => undefined;
  const logger: Logger = {
    level: 'debug',
    error: (...a) => {
      errors.push(a);
    },
    warn: noop,
    info: noop,
    debug: noop,
    child: () => logger,
  };
  return { logger, errors };
}

describe('smart banner (FR-224, WCAG 2.2 AA)', () => {
  let store: KeyValueStore;
  const handles: SmartBannerHandle[] = [];

  function show(options: Partial<SmartBannerOptions> = {}, platform: PlatformInfo = IOS_SAFARI, language: 'en' | 'sk' | 'auto' = 'en') {
    const { logger, errors } = silentLogger();
    const handle = createSmartBanner({ appName: 'Zak', openUrl: OPEN_URL, ...options }, { store, platform, language, logger });
    if (handle !== null) handles.push(handle);
    return { handle, errors };
  }

  beforeEach(() => {
    store = createMemoryStore('t');
    document.body.innerHTML = '';
  });

  afterEach(() => {
    for (const h of handles.splice(0)) h.destroy();
  });

  it('renders into an open shadow root that host CSS cannot reach', () => {
    const { handle } = show();
    expect(handle).not.toBeNull();
    expect(handle!.host.shadowRoot).toBe(handle!.shadowRoot);
    expect(document.querySelector('[data-dle-banner]')).toBe(handle!.host);
    // Nothing of the banner lives in the light DOM.
    expect(document.querySelector('a.cta')).toBeNull();
    expect(handle!.shadowRoot.querySelector('a.cta')).not.toBeNull();
    expect(handle!.shadowRoot.querySelector('style')!.textContent).toContain(':host{all:initial');
  });

  it('is a labelled region with a real anchor and a real button', () => {
    const { handle } = show();
    const root = handle!.shadowRoot;
    const region = root.querySelector('[role="region"]')!;
    expect(region.getAttribute('aria-label')).toBe('App banner');
    expect(region.getAttribute('lang')).toBe('en');

    const cta = root.querySelector<HTMLAnchorElement>('a.cta')!;
    expect(cta.tagName).toBe('A');
    expect(cta.href).toBe(OPEN_URL);
    expect(cta.textContent).toBe('Open');
    expect(cta.getAttribute('aria-label')).toBe('Open: Zak');
    expect(cta.rel).toBe('noopener');
    expect(cta.target).toBe('');

    const close = root.querySelector<HTMLButtonElement>('button.x')!;
    expect(close.tagName).toBe('BUTTON');
    expect(close.type).toBe('button');
    expect(close.getAttribute('aria-label')).toBe('Dismiss banner');
  });

  it('ships a visible focus ring, 44 px targets, reduced-motion and colour-scheme handling', () => {
    const css = show().handle!.shadowRoot.querySelector('style')!.textContent;
    expect(css).toContain(':focus-visible{outline:3px solid');
    expect(css).toContain('min-height:44px');
    expect(css).toContain('width:44px;height:44px');
    expect(css).toContain('@media (prefers-reduced-motion:no-preference)');
    expect(css).toContain('@media (prefers-color-scheme:dark)');
    expect(css).not.toMatch(/prefers-reduced-motion:\s*reduce/);
  });

  it('applies the requested theme class', () => {
    expect(show({ theme: 'dark' }).handle!.shadowRoot.querySelector('.b')!.className).toBe('b dark');
    expect(show({ theme: 'light' }).handle!.shadowRoot.querySelector('.b')!.className).toBe('b light');
    expect(show().handle!.shadowRoot.querySelector('.b')!.className).toBe('b auto');
  });

  it('speaks Slovak when asked, or when the browser does', () => {
    const sk = show({}, IOS_SAFARI, 'sk').handle!.shadowRoot;
    expect(sk.querySelector('a.cta')!.textContent).toBe('Otvoriť');
    expect(sk.querySelector('button.x')!.getAttribute('aria-label')).toBe('Zavrieť banner');
    expect(sk.querySelector('[role="region"]')!.getAttribute('lang')).toBe('sk');
    expect(sk.querySelector('.s')!.textContent).toBe(BANNER_STRINGS.sk.tagline);

    setNavigatorLanguage('sk-SK');
    expect(resolveLanguage('auto')).toBe('sk');
    expect(show({}, IOS_SAFARI, 'auto').handle!.shadowRoot.querySelector('a.cta')!.textContent).toBe('Otvoriť');
    setNavigatorLanguage('de-DE');
    expect(resolveLanguage('auto')).toBe('en');
    expect(resolveLanguage('sk', 'en-US')).toBe('sk');
  });

  it('localises the tagline and accepts string overrides', () => {
    const root = show({ tagline: { en: 'Deals inside', sk: 'Zľavy v aplikácii' }, strings: { open: 'Get app' } }, IOS_SAFARI, 'sk').handle!.shadowRoot;
    expect(root.querySelector('.s')!.textContent).toBe('Zľavy v aplikácii');
    expect(root.querySelector('a.cta')!.textContent).toBe('Get app');
  });

  it('escapes text: app names are content, never markup', () => {
    const root = show({ appName: '<img src=x onerror=alert(1)>' }).handle!.shadowRoot;
    expect(root.querySelector('img')).toBeNull();
    expect(root.querySelector('.n')!.textContent).toBe('<img src=x onerror=alert(1)>');
  });

  it('dismisses with the button, remembers it and suppresses the next banner', () => {
    const onDismiss = vi.fn();
    const { handle } = show({ onDismiss, dismissForDays: 7 });
    handle!.shadowRoot.querySelector<HTMLButtonElement>('button.x')!.click();

    expect(onDismiss).toHaveBeenCalledWith('button');
    expect(document.querySelector('[data-dle-banner]')).toBeNull();
    expect(Number(store.get(DISMISSED_UNTIL_KEY))).toBeGreaterThan(Date.now() + 6 * 86400000);
    expect(isBannerDismissed(store)).toBe(true);
    expect(show().handle).toBeNull();
    // The stored value is a timestamp, not an identifier.
    expect(store.get(DISMISSED_UNTIL_KEY)).toMatch(/^\d+$/);
  });

  it('dismisses on Escape from anywhere on the page, once', () => {
    const onDismiss = vi.fn();
    show({ onDismiss });
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    expect(onDismiss).toHaveBeenCalledTimes(1);
    expect(onDismiss).toHaveBeenCalledWith('escape');
    expect(document.querySelector('[data-dle-banner]')).toBeNull();
  });

  it('forgets an expired dismissal', () => {
    store.set(DISMISSED_UNTIL_KEY, String(Date.now() - 1));
    expect(isBannerDismissed(store)).toBe(false);
    expect(store.get(DISMISSED_UNTIL_KEY)).toBeNull();
    expect(show().handle).not.toBeNull();
  });

  it('destroy() removes the banner without remembering a dismissal', () => {
    const { handle } = show();
    handle!.destroy();
    expect(document.querySelector('[data-dle-banner]')).toBeNull();
    expect(store.get(DISMISSED_UNTIL_KEY)).toBeNull();
    expect(show().handle).not.toBeNull();
  });

  it('lets the anchor do the navigation: the click handler observes and never cancels', () => {
    const onOpen = vi.fn();
    const { handle } = show({ onOpen });
    const cta = handle!.shadowRoot.querySelector<HTMLAnchorElement>('a.cta')!;
    let defaultPreventedBySdk: boolean | null = null;
    cta.addEventListener('click', (event) => {
      defaultPreventedBySdk = event.defaultPrevented;
      event.preventDefault(); // keep jsdom from attempting a navigation in this test only
    });
    cta.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
    expect(onOpen).toHaveBeenCalledWith(OPEN_URL);
    expect(defaultPreventedBySdk).toBe(false);
  });

  it('shows the in-app hint only inside an in-app webview', () => {
    expect(show({}, IOS_INSTAGRAM).handle!.shadowRoot.querySelector('.h')!.textContent).toBe(BANNER_STRINGS.en.inAppHint);
    expect(show({}, IOS_SAFARI).handle!.shadowRoot.querySelector('.h')).toBeNull();
  });

  it('is suppressed on desktop by default, for crawlers always, and honours showOn', () => {
    expect(show({}, DESKTOP).handle).toBeNull();
    expect(show({}, CRAWLER).handle).toBeNull();
    expect(show({ showOn: ['desktop'] }, DESKTOP).handle).not.toBeNull();
    expect(show({ enabled: false }).handle).toBeNull();
  });

  it('refuses a custom-scheme openUrl and a missing appName', () => {
    const scheme = show({ openUrl: 'zak://promo?token=abc' });
    expect(scheme.handle).toBeNull();
    expect(scheme.errors.length).toBe(1);
    expect(show({ openUrl: 'javascript:alert(1)' }).handle).toBeNull();
    expect(show({ appName: '  ' }).handle).toBeNull();
  });

  it('accepts only https: and data:image icons, decorative for assistive tech', () => {
    expect(show({ iconUrl: 'http://cdn.example.com/icon.png' }).handle!.shadowRoot.querySelector('img')).toBeNull();
    const img = show({ iconUrl: 'data:image/png;base64,iVBORw0KGgo=' }).handle!.shadowRoot.querySelector('img')!;
    expect(img.alt).toBe('');
    expect(img.getAttribute('aria-hidden')).toBe('true');
    expect(show({ iconUrl: 'https://cdn.example.com/icon.png' }).handle!.shadowRoot.querySelector('img')).not.toBeNull();
  });

  it('positions at the bottom by default and at the top on request', () => {
    expect(show().handle!.host.getAttribute('data-pos')).toBe('bottom');
    expect(show({ position: 'top' }).handle!.host.getAttribute('data-pos')).toBe('top');
    expect(show({ zIndex: 42 }).handle!.host.style.zIndex).toBe('42');
  });
});
