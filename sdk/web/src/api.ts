import type { ConsentWire } from './consent.js';
import type { Logger } from './log.js';
import { hex, randomBytes } from './random.js';
import type { DevicePlatform, DleEvent, DleEventType, MatchType, ResolveLink, ResolveResult } from './types.js';

/**
 * The HTTP layer and the one place where camelCase meets snake_case.
 *
 * Every wire interface below lists its members in the property order of
 * `Dle.Domain.Contracts.SdkContracts`, and the builders write them in that order, so a
 * serialised body is byte-comparable with the literals in
 * `tests/Dle.ContractTests/Wire/SdkWireContractTests.cs`. Absent optional members are
 * omitted, never written as `null` (the server serialises with `WhenWritingNull`).
 */

// ---------------------------------------------------------------------------------------
// Problem types (RFC 9457) — the subset the SDK plane can answer with, from ProblemCodes.cs
// ---------------------------------------------------------------------------------------

/** Base URI every problem `type` is derived from (`ProblemCodes.Base`). */
export const PROBLEM_BASE = 'https://docs.dle.dev/problems/';

/** Stable problem `type` URIs an integrator may branch on. */
export const PROBLEM_TYPES = {
  validationFailed: `${PROBLEM_BASE}validation-failed`,
  rateLimited: `${PROBLEM_BASE}rate-limited`,
  unauthorized: `${PROBLEM_BASE}unauthorized`,
  forbidden: `${PROBLEM_BASE}forbidden`,
  claimCodeInvalid: `${PROBLEM_BASE}claim-code-invalid`,
  dependencyUnavailable: `${PROBLEM_BASE}dependency-unavailable`,
} as const;

/** An RFC 9457 problem document as the engine sends it. */
export interface ProblemDocument {
  readonly type?: string;
  readonly title?: string;
  readonly status?: number;
  readonly detail?: string;
  readonly instance?: string;
  /** Extension members (`errors`, `reason`, `can_reissue`, …). */
  readonly [extension: string]: unknown;
}

export type DleErrorCode = 'config' | 'network' | 'timeout' | 'http' | 'malformed';

/** Every failure the SDK surfaces. `retriable` tells the queue whether to keep the events. */
export class DleError extends Error {
  readonly code: DleErrorCode;
  readonly status: number | undefined;
  readonly problem: ProblemDocument | undefined;
  /** Server-provided back-off from `Retry-After`, in milliseconds. */
  readonly retryAfterMs: number | undefined;
  readonly retriable: boolean;
  readonly cause: unknown;

  constructor(
    code: DleErrorCode,
    message: string,
    extra: {
      status?: number;
      problem?: ProblemDocument;
      retryAfterMs?: number;
      retriable?: boolean;
      cause?: unknown;
    } = {},
  ) {
    super(message);
    this.name = 'DleError';
    this.code = code;
    this.status = extra.status;
    this.problem = extra.problem;
    this.retryAfterMs = extra.retryAfterMs;
    this.retriable = extra.retriable ?? false;
    this.cause = extra.cause;
  }

  /** The problem `type` URI, when the server answered with a problem document. */
  get problemType(): string | undefined {
    return this.problem?.type;
  }
}

// ---------------------------------------------------------------------------------------
// Wire shapes
// ---------------------------------------------------------------------------------------

/** `DeviceSignalsDto`. Coarse by design; never a stable identifier. */
export interface DeviceSignalsWire {
  readonly language?: string;
  readonly screen?: string;
  readonly tz_offset?: number;
  readonly device_model?: string;
}

/** `ResolveRequestDto`. */
export interface ResolveRequestWire {
  readonly install_id: string;
  readonly platform: DevicePlatform;
  readonly app_version?: string;
  readonly os_version?: string;
  readonly referrer?: string;
  readonly claim_code?: string;
  readonly login_key?: string;
  readonly signals?: DeviceSignalsWire;
  readonly consent?: ConsentWire;
}

/** `EventDto`. */
export interface EventWire {
  readonly type: DleEventType;
  readonly name?: string;
  readonly url?: string;
  readonly value?: number;
  readonly currency?: string;
  readonly ts?: string;
  readonly properties?: Readonly<Record<string, string>>;
}

/** `EventBatchDto`. */
export interface EventBatchWire {
  readonly install_id: string;
  readonly platform?: DevicePlatform;
  readonly app_version?: string;
  readonly events: readonly EventWire[];
}

/** `EventBatchAcceptedDto`. */
export interface EventBatchAcceptedWire {
  readonly accepted: number;
  readonly rejected: number;
}

/** `EventBatchDto.MaxEventsPerBatch`. */
export const MAX_EVENTS_PER_BATCH = 100;

/** `InstallReferrerParser.MaxReferrerLength`. */
export const MAX_REFERRER_LENGTH = 1024;

const MATCH_TYPES: readonly string[] = ['none', 'install_referrer', 'login', 'claim_code', 'probabilistic', 'direct_open'];

// ---------------------------------------------------------------------------------------
// Builders (pure)
// ---------------------------------------------------------------------------------------

/**
 * Remove `undefined` members so an absent value is absent, not present-but-empty. Member
 * order is insertion order, which is why every literal below is written in contract order.
 */
function compact<T extends object>(value: T): T {
  return Object.fromEntries(Object.entries(value).filter(([, v]) => v !== undefined)) as T;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function stringMap(value: unknown): Record<string, string> {
  const out: Record<string, string> = {};
  if (isRecord(value)) {
    for (const key of Object.keys(value)) {
      const item = value[key];
      if (typeof item === 'string') out[key] = item;
    }
  }
  return out;
}

export interface ResolveRequestInput {
  readonly installId: string;
  readonly platform: DevicePlatform;
  readonly appVersion?: string | undefined;
  readonly osVersion?: string | undefined;
  readonly referrer?: string | undefined;
  readonly claimCode?: string | undefined;
  readonly loginKey?: string | undefined;
  /** Already consent-gated by the caller: `undefined` means the member is omitted entirely. */
  readonly signals?: DeviceSignalsWire | undefined;
  readonly consent?: ConsentWire | undefined;
}

/** Build a `ResolveRequestDto` body. Members appear in contract order; absent ones are omitted. */
export function buildResolveRequest(input: ResolveRequestInput): ResolveRequestWire {
  return compact({
    install_id: input.installId,
    platform: input.platform,
    app_version: input.appVersion,
    os_version: input.osVersion,
    referrer: input.referrer?.slice(0, MAX_REFERRER_LENGTH),
    claim_code: input.claimCode,
    login_key: input.loginKey,
    signals: input.signals,
    consent: input.consent,
  });
}

/** Translate one public event into an `EventDto`, in contract member order. */
export function toEventWire(event: DleEvent): EventWire {
  return compact({
    type: event.type,
    name: event.name,
    url: event.url,
    value: event.value !== undefined && Number.isFinite(event.value) ? event.value : undefined,
    currency: event.currency,
    ts: event.ts,
    properties: event.properties === undefined ? undefined : stringMap(event.properties),
  });
}

/** Build an `EventBatchDto` body. Throws when the batch exceeds the server's maximum. */
export function buildEventBatch(
  installId: string,
  events: readonly DleEvent[],
  meta: { readonly platform?: DevicePlatform | undefined; readonly appVersion?: string | undefined } = {},
): EventBatchWire {
  if (events.length === 0 || events.length > MAX_EVENTS_PER_BATCH) {
    throw new DleError('config', `a batch carries 1 to ${MAX_EVENTS_PER_BATCH} events, not ${events.length}`);
  }
  return compact({
    install_id: installId,
    platform: meta.platform,
    app_version: meta.appVersion,
    events: events.map(toEventWire),
  });
}

/**
 * Collect the coarse device signals of `DeviceSignalsDto`.
 *
 * Three values, each of which millions of devices share: the UI language, the physical
 * screen size and the UTC offset. Nothing is hashed, nothing is combined, and the caller
 * only invokes this when the operator opted in AND the user granted attribution consent.
 * `tz_offset` is the offset *from* UTC in minutes (UTC+2 → 120), which is the negation of
 * `Date.prototype.getTimezoneOffset()`.
 */
export interface SignalsView {
  readonly language?: string | undefined;
  readonly width?: number | undefined;
  readonly height?: number | undefined;
  readonly pixelRatio?: number | undefined;
  readonly timezoneOffset?: number | undefined;
}

export function collectSignals(view: SignalsView = currentView()): DeviceSignalsWire {
  const { language, width, height, timezoneOffset } = view;
  const ratio = view.pixelRatio !== undefined && view.pixelRatio > 0 ? view.pixelRatio : 1;
  const sized = width !== undefined && height !== undefined && width > 0 && height > 0;
  return compact({
    language: language === '' ? undefined : language,
    screen: sized ? `${Math.round(width * ratio)}x${Math.round(height * ratio)}` : undefined,
    tz_offset: timezoneOffset !== undefined && Number.isFinite(timezoneOffset) ? -timezoneOffset : undefined,
  });
}

function currentView(): SignalsView {
  const g = globalThis as {
    navigator?: { language?: string };
    screen?: { width?: number; height?: number };
    devicePixelRatio?: number;
  };
  return {
    language: g.navigator?.language,
    width: g.screen?.width,
    height: g.screen?.height,
    pixelRatio: g.devicePixelRatio,
    timezoneOffset: new Date().getTimezoneOffset(),
  };
}

// ---------------------------------------------------------------------------------------
// Response parsing
// ---------------------------------------------------------------------------------------

/**
 * Parse a `ResolveResponseDto`.
 *
 * The two members that carry the meaning of an attribution — `match_type` and `confidence`
 * — are mandatory. A body without them is refused rather than guessed at, because a guess
 * is exactly how a probabilistic hint ends up displayed as a certainty (FR-186, ADR-008).
 */
export function parseResolveResponse(value: unknown): ResolveResult {
  if (!isRecord(value)) throw new DleError('malformed', 'resolve response is not a JSON object');
  const matchType = value.match_type;
  if (typeof matchType !== 'string' || !MATCH_TYPES.includes(matchType)) {
    throw new DleError('malformed', 'resolve response has no valid match_type');
  }
  const confidence = value.confidence;
  if (typeof confidence !== 'number' || !Number.isFinite(confidence)) {
    throw new DleError('malformed', 'resolve response has no valid confidence');
  }
  const link = value.link;
  const expiresIn = value.expires_in;
  const str = (v: unknown): string | undefined => (typeof v === 'string' ? v : undefined);
  return compact({
    matched: value.matched === true,
    matchType: matchType as MatchType,
    confidence: Math.min(1, Math.max(0, confidence)),
    clickId: str(value.click_id),
    link:
      isRecord(link) && typeof link.id === 'string'
        ? compact<ResolveLink>({
            id: link.id,
            deeplinkPath: str(link.deeplink_path),
            campaign: str(link.campaign),
            title: str(link.title),
          })
        : undefined,
    params: stringMap(value.params),
    expiresIn: typeof expiresIn === 'number' && expiresIn > 0 ? Math.floor(expiresIn) : 0,
  });
}

/**
 * `true` only for a match the engine established deterministically with full confidence.
 * Use it before treating a resolve result as fact; a `probabilistic` match is a hint.
 */
export function isDeterministic(result: Pick<ResolveResult, 'matched' | 'matchType' | 'confidence'>): boolean {
  return result.matched && result.matchType !== 'none' && result.matchType !== 'probabilistic' && result.confidence >= 1;
}

// ---------------------------------------------------------------------------------------
// Trace context (NFR-12): one W3C `traceparent` per request so the SDK span and the server
// span it caused belong to one trace. Random ids, never derived from anything.
// ---------------------------------------------------------------------------------------

/** A fresh `traceparent` header value (version 00, sampled). */
export function createTraceparent(): string {
  return `00-${hex(randomBytes(16))}-${hex(randomBytes(8))}-01`;
}

// ---------------------------------------------------------------------------------------
// HTTP client
// ---------------------------------------------------------------------------------------

export interface ApiClientOptions {
  readonly endpoint: string;
  readonly sdkKey: string;
  readonly timeoutMs: number;
  readonly fetchImpl?: typeof fetch | undefined;
  readonly logger?: Logger | undefined;
}

export interface SendOptions {
  /**
   * Let the request outlive the page (`fetch` `keepalive`). Used on `pagehide`. The
   * `navigator.sendBeacon` API cannot carry the `Authorization` header this endpoint
   * requires (`DleKeyCredentialReader` reads headers only), so a beacon would be refused
   * with 401 — `fetch` with `keepalive` is the only unload-safe transport that authenticates.
   */
  readonly keepalive?: boolean;
}

export interface ApiClient {
  /** `POST /v1/resolve`. Returns the parsed body; the caller validates it. */
  resolve(body: ResolveRequestWire): Promise<unknown>;
  /** `POST /v1/events`. Resolves on 202. */
  sendEvents(body: EventBatchWire, options?: SendOptions): Promise<EventBatchAcceptedWire>;
}

/** Parse `Retry-After` (delay-seconds or HTTP-date) into milliseconds. */
export function parseRetryAfter(header: string | null, now: number = Date.now()): number | undefined {
  if (header === null || header === '') return undefined;
  const trimmed = header.trim();
  if (/^\d+$/.test(trimmed)) return Number(trimmed) * 1000;
  const at = Date.parse(trimmed);
  return Number.isNaN(at) ? undefined : Math.max(0, at - now);
}

/**
 * Drop trailing slashes from a URL the host application supplied. A loop rather than `/\/+$/`:
 * that expression backtracks quadratically on a long run of slashes that is not at the end of
 * the string, and the endpoint is host-application input (CodeQL js/polynomial-redos).
 */
export function stripTrailingSlashes(value: string): string {
  let end = value.length;
  while (end > 0 && value.charCodeAt(end - 1) === 47 /* '/' */) end--;
  return value.slice(0, end);
}

export function createApiClient(options: ApiClientOptions): ApiClient {
  const base = stripTrailingSlashes(options.endpoint);
  const fetchImpl = options.fetchImpl ?? (globalThis as { fetch?: typeof fetch }).fetch;

  async function post(path: string, body: unknown, keepalive: boolean): Promise<unknown> {
    if (typeof fetchImpl !== 'function') {
      throw new DleError('network', 'fetch is not available', { retriable: true });
    }
    const controller = typeof AbortController === 'function' ? new AbortController() : undefined;
    const timer = controller && setTimeout(() => controller.abort(), options.timeoutMs);
    let response: Response;
    try {
      response = await fetchImpl(base + path, {
        method: 'POST',
        headers: {
          'content-type': 'application/json',
          accept: 'application/json, application/problem+json',
          authorization: `Bearer ${options.sdkKey}`,
          traceparent: createTraceparent(),
        },
        body: JSON.stringify(body),
        credentials: 'omit',
        cache: 'no-store',
        // A bearer key must never be replayed to wherever a misconfigured proxy points.
        redirect: 'error',
        keepalive,
        signal: controller?.signal,
      });
    } catch (cause) {
      const timedOut = controller?.signal.aborted === true;
      throw new DleError(
        timedOut ? 'timeout' : 'network',
        timedOut ? `request timed out after ${options.timeoutMs} ms` : 'network request failed',
        { retriable: true, cause },
      );
    } finally {
      clearTimeout(timer);
    }

    let json: unknown;
    try {
      const text = await response.text();
      json = text === '' ? undefined : JSON.parse(text);
    } catch {
      json = undefined;
    }

    const status = response.status;
    if (response.ok) {
      if (json === undefined) throw new DleError('malformed', `HTTP ${status} without a JSON body`);
      return json;
    }
    const problem = isRecord(json) ? (json as ProblemDocument) : undefined;
    // Status codes only — the key, the body and the problem detail never reach the console.
    options.logger?.debug(`POST ${path} -> ${status}`);
    throw new DleError('http', problem?.title ?? `HTTP ${status}`, {
      status,
      problem,
      retryAfterMs: parseRetryAfter(response.headers.get('retry-after')),
      retriable: status === 429 || status === 408 || status >= 500,
    });
  }

  return {
    resolve: (body) => post('/v1/resolve', body, false),
    sendEvents: async (body, sendOptions = {}) => {
      const json = await post('/v1/events', body, sendOptions.keepalive === true);
      const n = (k: string): number => {
        const v = isRecord(json) ? json[k] : undefined;
        return typeof v === 'number' ? v : 0;
      };
      return { accepted: n('accepted'), rejected: n('rejected') };
    },
  };
}
