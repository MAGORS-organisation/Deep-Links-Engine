import { describe, expect, it } from 'vitest';

import pkg from '../package.json';
import {
  DleError,
  MAX_EVENTS_PER_BATCH,
  buildEventBatch,
  buildResolveRequest,
  collectSignals,
  isDeterministic,
  parseResolveResponse,
} from '../src/api';
import { VERSION, referrerFromSearch } from '../src/index';
import type { DleEvent } from '../src/types';
import { B72 } from './helpers';

describe('wire contract §B.7.2 (literals from SdkWireContractTests.cs)', () => {
  it('serialises ResolveRequestDto byte-for-byte', () => {
    const body = buildResolveRequest({
      installId: B72.INSTALL_ID,
      platform: 'android',
      appVersion: '3.4.1',
      osVersion: '15',
      referrer: 'dl_cid%3DaB3xK9pQ%26utm_source%3Dfb',
      claimCode: undefined,
      signals: { language: 'sk-SK', screen: '1080x2400', tz_offset: 120 },
      consent: { analytics: true, attribution: true, ts: '2026-09-03T10:00:00+00:00' },
    });
    expect(JSON.stringify(body)).toBe(B72.RESOLVE_REQUEST);
  });

  it('serialises EventBatchDto byte-for-byte', () => {
    const body = buildEventBatch(B72.INSTALL_ID, [
      { type: 'link_open', url: 'https://link.zak.sk/aB3xK9pQ', ts: '2026-09-03T10:00:00+00:00' },
      { type: 'conversion', name: 'purchase', value: 24.9, currency: 'EUR', ts: '2026-09-03T10:05:00+00:00' },
    ]);
    expect(JSON.stringify(body)).toBe(B72.EVENT_BATCH);
  });

  it('adds platform and app_version in contract order when present', () => {
    const body = buildEventBatch(B72.INSTALL_ID, [{ type: 'session' }], { platform: 'ios', appVersion: '1.2.3' });
    expect(Object.keys(body)).toEqual(['install_id', 'platform', 'app_version', 'events']);
  });

  it('parses ResolveResponseDto into the camelCase surface', () => {
    const result = parseResolveResponse(JSON.parse(B72.RESOLVE_RESPONSE));
    expect(result).toEqual({
      matched: true,
      matchType: 'install_referrer',
      confidence: 1,
      clickId: 'aB3xK9pQ',
      link: { id: '7286414500000000001', deeplinkPath: '/promo/jesen', campaign: 'jesen26' },
      params: { utm_source: 'fb', utm_campaign: 'jesen26', promo: 'AUTUMN20' },
      expiresIn: 0,
    });
    expect(isDeterministic(result)).toBe(true);
  });

  it('treats an unmatched install as a result, not an error', () => {
    const result = parseResolveResponse(JSON.parse(B72.RESOLVE_RESPONSE_UNMATCHED));
    expect(result).toEqual({ matched: false, matchType: 'none', confidence: 0, params: {}, expiresIn: 0 });
    expect('clickId' in result).toBe(false);
    expect('link' in result).toBe(false);
    expect(isDeterministic(result)).toBe(false);
  });

  it('parses the accepted acknowledgement literal', () => {
    expect(JSON.parse(B72.EVENT_BATCH_ACCEPTED)).toEqual({ accepted: 2, rejected: 0 });
  });

  it('keeps params keys in their original spelling', () => {
    const result = parseResolveResponse({
      matched: true,
      match_type: 'direct_open',
      confidence: 1.0,
      params: { utmSource: 'fb', PromoCode: 'AUTUMN20' },
      expires_in: 0,
    });
    expect(result.params).toEqual({ utmSource: 'fb', PromoCode: 'AUTUMN20' });
  });

  it('never emits camelCase member names', () => {
    const json = JSON.stringify(
      buildResolveRequest({
        installId: 'a',
        platform: 'ios',
        appVersion: '1',
        osVersion: '17.5',
        claimCode: 'ABC123',
        loginKey: 'k',
        signals: { tz_offset: 60 },
        consent: { analytics: true, attribution: true },
      }),
    );
    expect(json).not.toMatch(/installId|appVersion|osVersion|claimCode|loginKey|tzOffset|matchType/);
    expect(JSON.parse(json)).toMatchObject({ claim_code: 'ABC123', login_key: 'k', os_version: '17.5' });
  });

  it('omits the signals member entirely when it is not supplied', () => {
    const body = buildResolveRequest({ installId: 'a', platform: 'ios', signals: undefined });
    expect('signals' in body).toBe(false);
    expect(JSON.stringify(body)).toBe('{"install_id":"a","platform":"ios"}');
  });

  it('clips the referrer to InstallReferrerParser.MaxReferrerLength', () => {
    const body = buildResolveRequest({ installId: 'a', platform: 'ios', referrer: 'x'.repeat(2000) });
    expect(body.referrer).toHaveLength(1024);
  });

  it('enforces EventBatchDto.MaxEventsPerBatch', () => {
    expect(MAX_EVENTS_PER_BATCH).toBe(100);
    const events: DleEvent[] = Array.from({ length: 101 }, () => ({ type: 'session' }));
    expect(() => buildEventBatch('a', events)).toThrow(DleError);
    expect(() => buildEventBatch('a', [])).toThrow(DleError);
    expect(buildEventBatch('a', events.slice(0, 100)).events).toHaveLength(100);
  });

  it('drops non-string properties and non-finite values from events', () => {
    const batch = buildEventBatch('a', [
      { type: 'custom', name: 'x', value: Number.NaN, properties: { ok: 'yes', bad: 1 as unknown as string } },
    ]);
    expect(batch.events[0]).toEqual({ type: 'custom', name: 'x', properties: { ok: 'yes' } });
  });
});

describe('malformed resolve responses are refused, never guessed', () => {
  it('rejects a body without match_type', () => {
    expect(() => parseResolveResponse({ matched: true, confidence: 1 })).toThrow(DleError);
  });

  it('rejects a body without confidence', () => {
    expect(() => parseResolveResponse({ matched: true, match_type: 'login' })).toThrow(DleError);
  });

  it('rejects an unknown match_type', () => {
    expect(() => parseResolveResponse({ matched: true, match_type: 'magic', confidence: 1 })).toThrow(/match_type/);
  });

  it('rejects non-objects', () => {
    expect(() => parseResolveResponse('yes')).toThrow(DleError);
    expect(() => parseResolveResponse(null)).toThrow(DleError);
  });

  it('never treats a probabilistic match as deterministic', () => {
    const result = parseResolveResponse({ matched: true, match_type: 'probabilistic', confidence: 0.62, params: {} });
    expect(result.matchType).toBe('probabilistic');
    expect(result.confidence).toBe(0.62);
    expect(isDeterministic(result)).toBe(false);
    // Even a server bug reporting 1.0 does not make a probabilistic match certain.
    expect(isDeterministic({ matched: true, matchType: 'probabilistic', confidence: 1 })).toBe(false);
  });

  it('clamps confidence into [0, 1]', () => {
    expect(parseResolveResponse({ matched: true, match_type: 'login', confidence: 7 }).confidence).toBe(1);
    expect(parseResolveResponse({ matched: false, match_type: 'none', confidence: -1 }).confidence).toBe(0);
  });
});

describe('device signals', () => {
  it('reports UTC offset as minutes east of UTC, physical screen pixels and the BCP 47 tag', () => {
    // Slovakia in September: UTC+2, getTimezoneOffset() === -120, wire tz_offset === 120.
    expect(collectSignals({ language: 'sk-SK', width: 360, height: 800, pixelRatio: 3, timezoneOffset: -120 })).toEqual({
      language: 'sk-SK',
      screen: '1080x2400',
      tz_offset: 120,
    });
  });

  it('omits what it cannot read instead of inventing it', () => {
    expect(collectSignals({})).toEqual({});
    expect(collectSignals({ width: 0, height: 0, language: '' })).toEqual({});
  });

  it('reads the real browser without throwing', () => {
    const signals = collectSignals();
    expect(Object.keys(signals).every((k) => ['language', 'screen', 'tz_offset'].includes(k))).toBe(true);
    expect('device_model' in signals).toBe(false);
  });
});

describe('referrer from the page URL', () => {
  it('builds the Install-Referrer form the server parses', () => {
    expect(referrerFromSearch('?dl_cid=aB3xK9pQ&utm_source=fb&fbclid=xyz&user=alice')).toBe(
      'dl_cid%3DaB3xK9pQ%26utm_source%3Dfb',
    );
  });

  it('reads nothing at all when there is no dl_cid', () => {
    expect(referrerFromSearch('?utm_source=fb&user=alice')).toBeUndefined();
    expect(referrerFromSearch('')).toBeUndefined();
    expect(referrerFromSearch('?')).toBeUndefined();
  });
});

describe('version', () => {
  it('matches package.json', () => {
    expect(VERSION).toBe(pkg.version);
  });
});
