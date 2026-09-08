import { afterEach, describe, expect, it, vi } from 'vitest';

import { DleError, PROBLEM_TYPES, createApiClient, createTraceparent, parseRetryAfter } from '../src/api';
import type { Logger } from '../src/log';
import { createFetchMock, jsonResponse } from './helpers';

const SDK_KEY = 'dle_pk_0123456789abcdef_SECRETSECRET';

function spyLogger(): { logger: Logger; lines: unknown[][] } {
  const lines: unknown[][] = [];
  const push = (...args: unknown[]): void => {
    lines.push(args);
  };
  const logger: Logger = { level: 'debug', error: push, warn: push, info: push, debug: push, child: () => logger };
  return { logger, lines };
}

describe('api client', () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  it('posts JSON with the bearer key, a traceparent and no credentials', async () => {
    const { fetch, calls } = createFetchMock(() => jsonResponse(202, { accepted: 1, rejected: 0 }));
    const api = createApiClient({ endpoint: 'https://links.example.sk/', sdkKey: SDK_KEY, timeoutMs: 4000, fetchImpl: fetch });

    const ack = await api.sendEvents({ install_id: 'a', events: [{ type: 'session' }] });

    expect(ack).toEqual({ accepted: 1, rejected: 0 });
    expect(calls).toHaveLength(1);
    const call = calls[0]!;
    expect(call.url).toBe('https://links.example.sk/v1/events');
    expect(call.init.method).toBe('POST');
    expect(call.headers.authorization).toBe(`Bearer ${SDK_KEY}`);
    expect(call.headers['content-type']).toBe('application/json');
    expect(call.headers.traceparent).toMatch(/^00-[0-9a-f]{32}-[0-9a-f]{16}-01$/);
    expect(call.init.credentials).toBe('omit');
    expect(call.init.redirect).toBe('error');
    expect(call.init.keepalive).toBe(false);
    expect(call.rawBody).toBe('{"install_id":"a","events":[{"type":"session"}]}');
  });

  it('routes resolve to /v1/resolve and returns the raw body', async () => {
    const { fetch, calls } = createFetchMock(() =>
      jsonResponse(200, { matched: false, match_type: 'none', confidence: 0, params: {}, expires_in: 0 }),
    );
    const api = createApiClient({ endpoint: 'https://links.example.sk', sdkKey: SDK_KEY, timeoutMs: 4000, fetchImpl: fetch });
    const body = await api.resolve({ install_id: 'a', platform: 'ios' });
    expect(calls[0]!.url).toBe('https://links.example.sk/v1/resolve');
    expect(body).toMatchObject({ match_type: 'none' });
  });

  it('passes keepalive through for unload flushes', async () => {
    const { fetch, calls } = createFetchMock(() => jsonResponse(202, { accepted: 1 }));
    const api = createApiClient({ endpoint: 'https://links.example.sk', sdkKey: SDK_KEY, timeoutMs: 4000, fetchImpl: fetch });
    await api.sendEvents({ install_id: 'a', events: [{ type: 'session' }] }, { keepalive: true });
    expect(calls[0]!.init.keepalive).toBe(true);
  });

  it('uses a fresh traceparent per request', async () => {
    const { fetch, calls } = createFetchMock(() => jsonResponse(202, { accepted: 1 }));
    const api = createApiClient({ endpoint: 'https://links.example.sk', sdkKey: SDK_KEY, timeoutMs: 4000, fetchImpl: fetch });
    await api.sendEvents({ install_id: 'a', events: [{ type: 'session' }] });
    await api.sendEvents({ install_id: 'a', events: [{ type: 'session' }] });
    expect(calls[0]!.headers.traceparent).not.toBe(calls[1]!.headers.traceparent);
    expect(createTraceparent()).not.toMatch(/^00-0{32}-/);
  });

  it('turns 429 into a retriable error carrying Retry-After and the problem type', async () => {
    const { fetch } = createFetchMock(() =>
      jsonResponse(
        429,
        { type: PROBLEM_TYPES.rateLimited, title: 'Too many requests', status: 429, detail: 'slow down' },
        { 'retry-after': '30' },
      ),
    );
    const api = createApiClient({ endpoint: 'https://links.example.sk', sdkKey: SDK_KEY, timeoutMs: 4000, fetchImpl: fetch });
    const error = await api.sendEvents({ install_id: 'a', events: [{ type: 'session' }] }).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(DleError);
    const dle = error as DleError;
    expect(dle.code).toBe('http');
    expect(dle.status).toBe(429);
    expect(dle.retriable).toBe(true);
    expect(dle.retryAfterMs).toBe(30_000);
    expect(dle.problemType).toBe('https://docs.dle.dev/problems/rate-limited');
    expect(dle.problem?.detail).toBe('slow down');
  });

  it.each([
    [400, PROBLEM_TYPES.validationFailed, false],
    [401, PROBLEM_TYPES.unauthorized, false],
    [403, PROBLEM_TYPES.forbidden, false],
    [413, PROBLEM_TYPES.validationFailed, false],
    [408, undefined, true],
    [500, undefined, true],
    [503, PROBLEM_TYPES.dependencyUnavailable, true],
  ])('classifies HTTP %i (retriable: %s)', async (status, type, retriable) => {
    const { fetch } = createFetchMock(() => jsonResponse(status, type === undefined ? undefined : { type, status }));
    const api = createApiClient({ endpoint: 'https://links.example.sk', sdkKey: SDK_KEY, timeoutMs: 4000, fetchImpl: fetch });
    const error = (await api.resolve({ install_id: 'a', platform: 'ios' }).catch((e: unknown) => e)) as DleError;
    expect(error.code).toBe('http');
    expect(error.status).toBe(status);
    expect(error.retriable).toBe(retriable);
    expect(error.problemType).toBe(type);
  });

  it('times out with AbortController and reports a retriable timeout', async () => {
    vi.useFakeTimers();
    const hanging = vi.fn(
      (_input: RequestInfo | URL, init?: RequestInit) =>
        new Promise<Response>((_resolve, reject) => {
          init?.signal?.addEventListener('abort', () => {
            reject(new DOMException('aborted', 'AbortError'));
          });
        }),
    ) as unknown as typeof fetch;
    const api = createApiClient({ endpoint: 'https://links.example.sk', sdkKey: SDK_KEY, timeoutMs: 1500, fetchImpl: hanging });
    const pending = api.resolve({ install_id: 'a', platform: 'ios' });
    const settled = pending.catch((e: unknown) => e);
    await vi.advanceTimersByTimeAsync(1499);
    await vi.advanceTimersByTimeAsync(2);
    const error = (await settled) as DleError;
    expect(error).toBeInstanceOf(DleError);
    expect(error.code).toBe('timeout');
    expect(error.retriable).toBe(true);
    expect(error.message).toContain('1500');
  });

  it('reports a network failure as retriable', async () => {
    const failing = vi.fn(() => Promise.reject(new TypeError('Failed to fetch'))) as unknown as typeof fetch;
    const api = createApiClient({ endpoint: 'https://links.example.sk', sdkKey: SDK_KEY, timeoutMs: 4000, fetchImpl: failing });
    const error = (await api.resolve({ install_id: 'a', platform: 'ios' }).catch((e: unknown) => e)) as DleError;
    expect(error.code).toBe('network');
    expect(error.retriable).toBe(true);
    expect(error.cause).toBeInstanceOf(TypeError);
  });

  it('refuses a 2xx without a JSON body', async () => {
    const { fetch } = createFetchMock(() => new Response('<html>', { status: 200, headers: { 'content-type': 'text/html' } }));
    const api = createApiClient({ endpoint: 'https://links.example.sk', sdkKey: SDK_KEY, timeoutMs: 4000, fetchImpl: fetch });
    const error = (await api.resolve({ install_id: 'a', platform: 'ios' }).catch((e: unknown) => e)) as DleError;
    expect(error.code).toBe('malformed');
    expect(error.retriable).toBe(false);
  });

  it('never writes the SDK key to the console or into an error', async () => {
    const { logger, lines } = spyLogger();
    const { fetch } = createFetchMock(() => jsonResponse(401, { type: PROBLEM_TYPES.unauthorized, title: 'Unauthorized' }));
    const api = createApiClient({ endpoint: 'https://links.example.sk', sdkKey: SDK_KEY, timeoutMs: 4000, fetchImpl: fetch, logger });
    const error = (await api.resolve({ install_id: 'a', platform: 'ios' }).catch((e: unknown) => e)) as DleError;
    expect(error.message).not.toContain(SDK_KEY);
    expect(JSON.stringify(lines)).not.toContain(SDK_KEY);
    expect(lines.length).toBeGreaterThan(0);
  });

  it('parses Retry-After as seconds or as an HTTP date', () => {
    expect(parseRetryAfter('30')).toBe(30_000);
    expect(parseRetryAfter(null)).toBeUndefined();
    expect(parseRetryAfter('soon')).toBeUndefined();
    const now = Date.parse('2026-09-03T10:00:00Z');
    expect(parseRetryAfter('Thu, 03 Sep 2026 10:00:10 GMT', now)).toBe(10_000);
    expect(parseRetryAfter('Thu, 03 Sep 2026 09:00:00 GMT', now)).toBe(0);
  });
});
