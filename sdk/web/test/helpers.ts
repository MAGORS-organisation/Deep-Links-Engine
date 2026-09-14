import { vi } from 'vitest';

export interface RecordedCall {
  readonly url: string;
  readonly init: RequestInit;
  readonly body: Record<string, unknown> | undefined;
  readonly rawBody: string | undefined;
  readonly headers: Record<string, string>;
}

function normaliseHeaders(headers: HeadersInit | undefined): Record<string, string> {
  const out: Record<string, string> = {};
  if (headers === undefined) return out;
  if (headers instanceof Headers) {
    headers.forEach((value, key) => {
      out[key.toLowerCase()] = value;
    });
    return out;
  }
  const entries = Array.isArray(headers) ? headers : Object.entries(headers);
  for (const [key, value] of entries) out[key.toLowerCase()] = value;
  return out;
}

/** A `Response` carrying JSON (or a problem document for 4xx/5xx). */
export function jsonResponse(status: number, body?: unknown, headers: Record<string, string> = {}): Response {
  const contentType = status >= 400 ? 'application/problem+json' : 'application/json';
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: { 'content-type': contentType, ...headers },
  });
}

/** A `fetch` double that records every call and answers through `handler`. */
export function createFetchMock(handler: (call: RecordedCall, index: number) => Response | Promise<Response>): {
  fetch: typeof fetch;
  calls: RecordedCall[];
} {
  const calls: RecordedCall[] = [];
  const fn = vi.fn(async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const rawBody = typeof init?.body === 'string' ? init.body : undefined;
    const call: RecordedCall = {
      url: typeof input === 'string' ? input : input instanceof URL ? input.href : input.url,
      init: init ?? {},
      body: rawBody === undefined ? undefined : JSON.parse(rawBody),
      rawBody,
      headers: normaliseHeaders(init?.headers),
    };
    calls.push(call);
    return handler(call, calls.length - 1);
  });
  return { fetch: fn, calls };
}

/** Override `navigator.userAgent` for the current test (restored by `unstubGlobals`). */
export function setUserAgent(userAgent: string): void {
  Object.defineProperty(navigator, 'userAgent', { value: userAgent, configurable: true });
}

/** Override `navigator.language` for the current test. */
export function setNavigatorLanguage(language: string): void {
  Object.defineProperty(navigator, 'language', { value: language, configurable: true });
}

/** Minimal event target for injecting `window` / `document` into the queue. */
export function createFakeTarget(): {
  addEventListener(type: string, listener: () => void): void;
  removeEventListener(type: string, listener: () => void): void;
  dispatch(type: string): void;
  count(type: string): number;
  visibilityState: string;
  navigator: { onLine: boolean };
} {
  const listeners = new Map<string, Set<() => void>>();
  return {
    visibilityState: 'visible',
    navigator: { onLine: true },
    addEventListener(type, listener) {
      const set = listeners.get(type) ?? new Set<() => void>();
      set.add(listener);
      listeners.set(type, set);
    },
    removeEventListener(type, listener) {
      listeners.get(type)?.delete(listener);
    },
    dispatch(type) {
      for (const listener of listeners.get(type) ?? []) listener();
    },
    count(type) {
      return listeners.get(type)?.size ?? 0;
    },
  };
}

/** Literal wire bodies transcribed from tests/Dle.ContractTests/Wire/SdkWireContractTests.cs. */
export const B72 = {
  INSTALL_ID: '9f2c1b64-0c3f-4a8e-9c2d-8f4c1f0a77b1',
  RESOLVE_REQUEST:
    '{"install_id":"9f2c1b64-0c3f-4a8e-9c2d-8f4c1f0a77b1","platform":"android","app_version":"3.4.1","os_version":"15","referrer":"dl_cid%3DaB3xK9pQ%26utm_source%3Dfb","signals":{"language":"sk-SK","screen":"1080x2400","tz_offset":120},"consent":{"analytics":true,"attribution":true,"ts":"2026-09-03T10:00:00+00:00"}}',
  RESOLVE_RESPONSE:
    '{"matched":true,"match_type":"install_referrer","confidence":1.0,"click_id":"aB3xK9pQ","link":{"id":"7286414500000000001","deeplink_path":"/promo/jesen","campaign":"jesen26"},"params":{"utm_source":"fb","utm_campaign":"jesen26","promo":"AUTUMN20"},"expires_in":0}',
  RESOLVE_RESPONSE_UNMATCHED: '{"matched":false,"match_type":"none","confidence":0,"params":{},"expires_in":0}',
  EVENT_BATCH:
    '{"install_id":"9f2c1b64-0c3f-4a8e-9c2d-8f4c1f0a77b1","events":[{"type":"link_open","url":"https://link.zak.sk/aB3xK9pQ","ts":"2026-09-03T10:00:00+00:00"},{"type":"conversion","name":"purchase","value":24.9,"currency":"EUR","ts":"2026-09-03T10:05:00+00:00"}]}',
  EVENT_BATCH_ACCEPTED: '{"accepted":2,"rejected":0}',
} as const;
