import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { createDle, isDeterministic } from '../src/index';
import type { DleClient } from '../src/types';
import { B72, createFetchMock, jsonResponse } from './helpers';

const UNMATCHED = JSON.parse(B72.RESOLVE_RESPONSE_UNMATCHED) as Record<string, unknown>;

const UUID_V4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

describe('consent gates (spec §0.2, §E.6, FR-227, TC-145/146)', () => {
  const clients: DleClient[] = [];

  function client(extra: Partial<Parameters<typeof createDle>[0]> = {}, responder = () => jsonResponse(200, UNMATCHED)) {
    const mock = createFetchMock(responder);
    const dle = createDle({
      endpoint: 'https://links.example.sk',
      sdkKey: 'dle_test',
      platform: 'android',
      appVersion: '3.4.1',
      fetchImpl: mock.fetch,
      logLevel: 'silent',
      ...extra,
    });
    clients.push(dle);
    return { dle, calls: mock.calls };
  }

  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    history.replaceState({}, '', '/');
  });

  afterEach(() => {
    for (const c of clients.splice(0)) c.destroy();
  });

  it('starts fully denied and with no identifier at all', () => {
    const { dle } = client();
    expect(dle.getConsent()).toEqual({ analytics: false, attribution: false });
    expect(dle.getInstallId()).toBeNull();
    expect(localStorage.length).toBe(0);
  });

  it('with attribution consent false the request has no signals member, even when the operator opted in', async () => {
    const { dle, calls } = client({ probabilisticSignals: true });
    dle.setConsent({ analytics: true, attribution: false });

    await dle.resolve();

    const body = calls[0]!.body!;
    expect('signals' in body).toBe(false);
    expect(calls[0]!.rawBody).not.toContain('"signals"');
    expect(body.consent).toMatchObject({ analytics: true, attribution: false });
    expect(typeof (body.consent as { ts: unknown }).ts).toBe('string');
    // The install id exists for this page view but is memory-only: nothing in storage.
    expect(body.install_id).toMatch(UUID_V4);
    expect(localStorage.getItem('dle.install_id')).toBeNull();
  });

  it('with attribution consent false no persistent identifier is written', async () => {
    const { dle } = client();
    await dle.resolve();
    expect(Object.keys(localStorage).filter((k) => k.includes('install'))).toEqual([]);
    expect(dle.getInstallId()).toMatch(UUID_V4);
  });

  it('omits the consent member until a decision was recorded', async () => {
    const { calls } = client();
    await clients[0]!.resolve();
    expect('consent' in calls[0]!.body!).toBe(false);
  });

  it('with attribution consent true and the opt-in, coarse signals are sent and the id persists', async () => {
    const { dle, calls } = client({ probabilisticSignals: true });
    dle.setConsent({ attribution: true });

    await dle.resolve();

    const body = calls[0]!.body!;
    const signals = body.signals as Record<string, unknown>;
    expect(signals).toBeDefined();
    expect(Object.keys(signals).every((k) => ['language', 'screen', 'tz_offset'].includes(k))).toBe(true);
    expect('device_model' in signals).toBe(false);
    expect(localStorage.getItem('dle.install_id')).toBe(body.install_id);
    expect(dle.getInstallId()).toBe(body.install_id);
  });

  it('with attribution consent true but without the operator opt-in, no signals are sent', async () => {
    const { dle, calls } = client();
    dle.setConsent({ attribution: true });
    await dle.resolve();
    expect('signals' in calls[0]!.body!).toBe(false);
  });

  it('revoking attribution consent deletes the stored identifier and the cached result', async () => {
    const { dle } = client();
    dle.setConsent({ attribution: true });
    await dle.resolve();
    expect(localStorage.getItem('dle.install_id')).not.toBeNull();
    expect(sessionStorage.getItem('dle.resolve')).not.toBeNull();

    dle.setConsent({ attribution: false });

    expect(localStorage.getItem('dle.install_id')).toBeNull();
    expect(sessionStorage.getItem('dle.resolve')).toBeNull();
  });

  it('remembers a recorded decision across page loads and over the config default', () => {
    const first = client({ consent: { analytics: false } });
    first.dle.setConsent({ analytics: true, attribution: true });
    const second = client({ consent: { analytics: false, attribution: false } });
    expect(second.dle.getConsent()).toMatchObject({ analytics: true, attribution: true });
  });

  it('without analytics consent track() sends nothing, not even after flush', async () => {
    const { dle, calls } = client();
    dle.track({ type: 'session' });
    dle.track([{ type: 'custom', name: 'x' }]);
    expect(await dle.flush()).toBe(true);
    expect(calls).toHaveLength(0);
    expect(localStorage.getItem('dle.queue')).toBeNull();
  });

  it('with analytics consent track() reaches /v1/events in one batch', async () => {
    const { dle, calls } = client({}, () => jsonResponse(202, { accepted: 2, rejected: 0 }));
    dle.setConsent({ analytics: true });
    dle.track([{ type: 'link_open', url: 'https://link.zak.sk/aB3xK9pQ' }, { type: 'session' }]);
    expect(await dle.flush()).toBe(true);
    expect(calls).toHaveLength(1);
    expect(calls[0]!.url).toBe('https://links.example.sk/v1/events');
    const body = calls[0]!.body!;
    expect(body).toMatchObject({ platform: 'android', app_version: '3.4.1' });
    expect(body.events).toHaveLength(2);
  });

  it('revoking analytics consent discards what is still queued', async () => {
    const { dle, calls } = client();
    dle.setConsent({ analytics: true });
    dle.track({ type: 'session' });
    expect(localStorage.getItem('dle.queue')).not.toBeNull();
    dle.setConsent({ analytics: false });
    expect(localStorage.getItem('dle.queue')).toBeNull();
    await dle.flush();
    expect(calls).toHaveLength(0);
  });

  it('reads dl_cid and utm_* from the URL only with attribution consent', async () => {
    history.replaceState({}, '', '/landing?dl_cid=aB3xK9pQ&utm_source=fb&session=secret');
    const denied = client();
    await denied.dle.resolve();
    expect('referrer' in denied.calls[0]!.body!).toBe(false);

    sessionStorage.clear();
    const granted = client();
    granted.dle.setConsent({ attribution: true });
    await granted.dle.resolve();
    expect(granted.calls[0]!.body!.referrer).toBe('dl_cid%3DaB3xK9pQ%26utm_source%3Dfb');
    expect(granted.calls[0]!.rawBody).not.toContain('secret');
  });

  it('passes claim code and login key through under their wire names', async () => {
    const { dle, calls } = client();
    await dle.resolve({ claimCode: 'K7M2PX', loginKey: 'hashed-user' });
    expect(calls[0]!.body).toMatchObject({ claim_code: 'K7M2PX', login_key: 'hashed-user', platform: 'android', app_version: '3.4.1' });
  });

  it('caches a final result per tab and re-requests only with force', async () => {
    const { dle, calls } = client();
    const first = await dle.resolve();
    const second = await dle.resolve();
    expect(second).toEqual(first);
    expect(calls).toHaveLength(1);
    await dle.resolve({ force: true });
    expect(calls).toHaveLength(2);
  });

  it('surfaces a probabilistic match with its confidence, never as certain', async () => {
    const { dle } = client({}, () =>
      jsonResponse(200, { matched: true, match_type: 'probabilistic', confidence: 0.62, click_id: 'x', params: {}, expires_in: 3600 }),
    );
    dle.setConsent({ attribution: true });
    const result = await dle.resolve();
    expect(result.matchType).toBe('probabilistic');
    expect(result.confidence).toBe(0.62);
    expect(result.expiresIn).toBe(3600);
    expect(isDeterministic(result)).toBe(false);
  });

  it('refuses a malformed endpoint or a missing key at construction', () => {
    expect(() => createDle({ endpoint: 'links.example.sk', sdkKey: 'k' })).toThrow(/endpoint/);
    expect(() => createDle({ endpoint: 'https://links.example.sk', sdkKey: '' })).toThrow(/sdkKey/);
  });
});
