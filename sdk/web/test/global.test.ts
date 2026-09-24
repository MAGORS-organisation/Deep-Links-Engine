import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

function addScript(attributes: Record<string, string>): HTMLScriptElement {
  const script = document.createElement('script');
  for (const [key, value] of Object.entries(attributes)) script.setAttribute(key, value);
  document.head.appendChild(script);
  return script;
}

describe('global IIFE entry', () => {
  beforeEach(() => {
    vi.resetModules();
    document.head.innerHTML = '';
    document.body.innerHTML = '';
    localStorage.clear();
    sessionStorage.clear();
  });

  afterEach(async () => {
    const mod = await import('../src/global');
    mod.getInstance()?.destroy();
  });

  it('boots from data-endpoint / data-sdk-key / data-banner on its own script tag', async () => {
    addScript({
      'data-endpoint': 'https://links.example.sk',
      'data-sdk-key': 'dle_test',
      'data-banner': JSON.stringify({ appName: 'Zak', openUrl: 'https://links.example.sk/aB3xK9pQ', showOn: ['other', 'desktop', 'ios', 'android'] }),
      'data-language': 'sk',
    });

    const mod = await import('../src/global');
    const instance = mod.getInstance();

    expect(instance).not.toBeNull();
    expect(instance!.version).toBe(mod.VERSION);
    expect(instance!.getConsent()).toEqual({ analytics: false, attribution: false });
    const host = document.querySelector('[data-dle-banner]')!;
    expect(host).not.toBeNull();
    expect(host.shadowRoot!.querySelector('a.cta')!.textContent).toBe('Otvoriť');
    expect(mod.boot()).toBe(instance);
  });

  it('data-banner="true" uses the page title and URL', async () => {
    document.title = 'Zak – promo';
    addScript({
      'data-endpoint': 'https://links.example.sk',
      'data-sdk-key': 'dle_test',
      'data-banner': 'true',
    });
    const mod = await import('../src/global');
    const dle = mod.getInstance()!;
    const handle = dle.showSmartBanner({ appName: document.title, openUrl: location.href, showOn: ['other'] });
    expect(handle).not.toBeNull();
    expect(handle!.shadowRoot.querySelector('.n')!.textContent).toBe('Zak – promo');
  });

  it('does nothing without a configured script tag', async () => {
    const mod = await import('../src/global');
    expect(mod.getInstance()).toBeNull();
    expect(document.querySelector('[data-dle-banner]')).toBeNull();
  });

  it('exposes the programmatic API as well', async () => {
    const mod = await import('../src/global');
    expect(typeof mod.createDle).toBe('function');
    expect(typeof mod.detect).toBe('function');
    expect(mod.CHANNELS).toContain('in_app_tiktok');
  });
});
