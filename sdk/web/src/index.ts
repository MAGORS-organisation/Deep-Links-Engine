import {
  DleError,
  buildEventBatch,
  buildLoginHintRequest,
  buildResolveRequest,
  collectSignals,
  createApiClient,
  isDeterministic,
  isKnownMatchType,
  parseResolveResponse,
} from './api.js';
import { createConsent } from './consent.js';
import { createInstallId } from './install-id.js';
import { createLogger } from './log.js';
import { detect } from './platform.js';
import { createQueue, isValidEvent } from './queue.js';
import { createSmartBanner } from './smart-banner.js';
import { createStore, createSessionStore, readJson, writeJson } from './storage.js';
import { isSafeWebUrl } from './url.js';
import type {
  DevicePlatform,
  DleClient,
  DleConfig,
  DleEvent,
  LoginHintOptions,
  LoginHintResult,
  ResolveOptions,
  ResolveResult,
  SmartBannerHandle,
  SmartBannerOptions,
} from './types.js';

export type * from './types.js';
export {
  DleError,
  PROBLEM_BASE,
  PROBLEM_TYPES,
  MAX_EVENTS_PER_BATCH,
  buildEventBatch,
  buildResolveRequest,
  collectSignals,
  createTraceparent,
  isDeterministic,
  parseResolveResponse,
  parseRetryAfter,
  toEventWire,
} from './api.js';
export type {
  DeviceSignalsWire,
  DleErrorCode,
  EventBatchAcceptedWire,
  EventBatchWire,
  EventWire,
  ProblemDocument,
  ResolveRequestWire,
} from './api.js';
export type { ConsentWire } from './consent.js';
export {
  CHANNELS,
  detect,
  detectChannel,
  detectOsVersion,
  detectPlatform,
  isInAppChannel,
  requiresUserTapForAppLink,
} from './platform.js';
export type { PlatformHints } from './platform.js';
export { BANNER_STRINGS, resolveLanguage } from './smart-banner.js';
export { generateInstallId } from './install-id.js';

/** SDK version. Kept equal to `package.json` by a test. */
export const VERSION = '1.0.0';

const RESOLVE_CACHE_KEY = 'resolve';
const PLATFORMS: readonly DevicePlatform[] = ['ios', 'android', 'desktop', 'other'];

interface ResolveCacheEntry {
  readonly install_id: string;
  readonly stored_at: number;
  readonly result: ResolveResult;
}

/**
 * Build the Install-Referrer-shaped referrer from the page URL: `dl_cid` plus `utm_*`,
 * percent-encoded like the Android referrer (`dl_cid%3D…%26utm_source%3D…`). Nothing else
 * from the query string is read, and nothing at all unless a `dl_cid` is present.
 */
export function referrerFromSearch(search: string): string | undefined {
  if (search === '' || search === '?') return undefined;
  let params: URLSearchParams;
  try {
    params = new URLSearchParams(search);
  } catch {
    return undefined;
  }
  const clickId = params.get('dl_cid');
  if (clickId === null || clickId === '') return undefined;
  const picked = new URLSearchParams();
  picked.set('dl_cid', clickId);
  for (const [key, value] of params) {
    if (/^utm_[a-z0-9_]+$/i.test(key) && !picked.has(key)) picked.set(key, value);
  }
  return encodeURIComponent(picked.toString());
}

function validateConfig(config: DleConfig): void {
  if (typeof config.sdkKey !== 'string' || config.sdkKey.trim() === '') {
    throw new DleError('config', 'sdkKey is required');
  }
  if (!isSafeWebUrl(config.endpoint, config.allowInsecureHttp === true)) {
    throw new DleError(
      'config',
      'endpoint must be a valid https:// URL (http:// is allowed only for loopback hosts or with allowInsecureHttp)',
    );
  }
  if (config.platform !== undefined && !PLATFORMS.includes(config.platform)) {
    throw new DleError('config', `platform must be one of ${PLATFORMS.join(', ')}`);
  }
}

/** Create an SDK client. Nothing leaves the browser until consent says it may. */
export function createDle(config: DleConfig): DleClient {
  validateConfig(config);

  const logger = createLogger(config.logLevel ?? 'warn');
  const namespace = config.storageNamespace ?? 'dle';
  const store = createStore(namespace);
  const session = createSessionStore(namespace);
  const timeoutMs = config.timeoutMs ?? 4000;

  const consent = createConsent({ initial: config.consent, store, logger });
  const installId = createInstallId({ store, mayPersist: () => consent.canPersistInstallId(), logger });

  const detected = detect();
  const platformInfo = config.platform === undefined ? detected : { ...detected, platform: config.platform };

  const api = createApiClient({
    endpoint: config.endpoint,
    sdkKey: config.sdkKey,
    timeoutMs,
    fetchImpl: config.fetchImpl,
    logger: logger.child('api'),
  });

  const queue = createQueue({
    store,
    mayFlush: () => consent.canSendEvents(),
    maxSize: config.maxQueueSize,
    flushIntervalMs: config.flushIntervalMs,
    logger: logger.child('queue'),
    send: async (events, keepalive) => {
      await api.sendEvents(
        buildEventBatch(installId.get(), events, { platform: platformInfo.platform, appVersion: config.appVersion }),
        { keepalive },
      );
    },
  });

  let banner: SmartBannerHandle | null = null;
  let destroyed = false;

  // The cached answer carries the install id, so without attribution consent it stays in
  // memory for the page view, like the id itself, and never reaches sessionStorage.
  let memoryEntry: ResolveCacheEntry | null = null;
  let attributionGranted = consent.get().attribution;

  const loadEntry = (): ResolveCacheEntry | null =>
    consent.canPersistInstallId() ? readJson<ResolveCacheEntry>(session, RESOLVE_CACHE_KEY) : memoryEntry;

  const storeEntry = (entry: ResolveCacheEntry | null): void => {
    if (entry !== null && consent.canPersistInstallId()) {
      writeJson(session, RESOLVE_CACHE_KEY, entry);
      memoryEntry = null;
    } else {
      session.remove(RESOLVE_CACHE_KEY);
      memoryEntry = entry;
    }
  };

  const unsubscribe = consent.onChange((state) => {
    installId.reconcile();
    if (!state.analytics) queue.clear();
    if (!state.attribution) {
      installId.clear();
      storeEntry(null);
    } else if (!attributionGranted) {
      // An unmatched answer given before attribution consent (`consent_missing`, non-final
      // `expires_in`) is stale now: drop it so the next resolve asks again, as the iOS and
      // Android SDKs do. A final or matched answer moves to sessionStorage with the id.
      const entry = memoryEntry;
      storeEntry(entry !== null && (entry.result.matched || entry.result.expiresIn === 0) ? entry : null);
    }
    attributionGranted = state.attribution;
  });

  const readCache = (id: string): ResolveResult | null => {
    const entry = loadEntry();
    if (entry?.install_id !== id) return null;
    const { expiresIn } = entry.result;
    if (expiresIn > 0 && entry.stored_at + expiresIn * 1000 <= Date.now()) {
      storeEntry(null);
      return null;
    }
    // A newer SDK on the same origin may have stored a match type this version does not know.
    return isKnownMatchType(entry.result.matchType) ? entry.result : { ...entry.result, matchType: 'none' };
  };

  async function resolve(options: ResolveOptions = {}): Promise<ResolveResult> {
    const id = installId.get();
    if (options.force !== true) {
      const cached = readCache(id);
      // New evidence (a claim code, a login key, an explicit referrer) is worth a request
      // unless the cached answer is already deterministic, as in the iOS and Android SDKs.
      const given = (value: string | undefined): boolean => value !== undefined && value.trim() !== '';
      const evidence = given(options.claimCode) || given(options.loginKey) || given(options.referrer);
      if (cached !== null && !(evidence && !isDeterministic(cached))) return cached;
    }

    const attribution = consent.canReadLinkParams();
    const search = (globalThis as { location?: { search?: string } }).location?.search;
    const referrer =
      options.referrer ??
      (attribution && config.readUrlParams !== false && typeof search === 'string'
        ? referrerFromSearch(search)
        : undefined);

    // Consent false: no `signals` member at all — not an empty object.
    const signals = consent.canSendDeviceSignals(config.probabilisticSignals === true) ? collectSignals() : undefined;

    const body = buildResolveRequest({
      installId: id,
      platform: platformInfo.platform,
      appVersion: config.appVersion,
      osVersion: platformInfo.osVersion,
      referrer,
      claimCode: options.claimCode,
      loginKey: options.loginKey,
      signals,
      consent: consent.toWire(),
    });

    const result = parseResolveResponse(await api.resolve(body), (raw) =>
      logger.warn(`unknown match_type '${raw}' from the engine, treated as none; update the SDK`),
    );
    storeEntry({ install_id: id, stored_at: Date.now(), result });
    logger.debug('resolved', result.matchType, result.confidence);
    return result;
  }

  function clickIdFromLocation(): string | undefined {
    const search = (globalThis as { location?: { search?: string } }).location?.search;
    if (typeof search !== 'string' || search === '' || search === '?') return undefined;
    try {
      const cid = new URLSearchParams(search).get('dl_cid');
      return cid !== null && cid !== '' ? cid : undefined;
    } catch {
      return undefined;
    }
  }

  async function recordLoginHint(options: LoginHintOptions): Promise<LoginHintResult> {
    if (destroyed || !consent.canReadLinkParams()) return { accepted: false };
    const clickId = options.clickId ?? (config.readUrlParams !== false ? clickIdFromLocation() : undefined);
    if (clickId === undefined && options.url === undefined) return { accepted: false };
    const res = await api.sendLoginHint(
      buildLoginHintRequest({
        loginKey: options.loginKey,
        clickId,
        url: options.url,
        deeplinkPath: options.deeplinkPath,
        consent: consent.toWire(),
      }),
    );
    return { accepted: res.accepted, expiresAt: res.expires_at };
  }

  function track(input: DleEvent | readonly DleEvent[]): void {
    if (destroyed) return;
    const events = Array.isArray(input) ? (input as readonly DleEvent[]) : [input as DleEvent];
    if (!consent.canSendEvents()) {
      logger.debug(`dropped ${events.length} event(s): no analytics consent`);
      return;
    }
    for (const event of events) {
      if (!isValidEvent(event)) {
        logger.warn('dropped event with unknown type', (event as { type?: unknown }).type);
        continue;
      }
      queue.enqueue(event);
    }
  }

  function showSmartBanner(options: SmartBannerOptions | undefined = config.smartBanner): SmartBannerHandle | null {
    if (destroyed || options === undefined) return null;
    if (banner !== null) banner.destroy();
    banner = createSmartBanner(options, {
      store,
      platform: platformInfo,
      language: config.language ?? 'auto',
      logger: logger.child('banner'),
    });
    return banner;
  }

  function hideSmartBanner(): void {
    banner?.destroy();
    banner = null;
  }

  const client: DleClient = {
    version: VERSION,
    resolve,
    recordLoginHint,
    track,
    flush: () => queue.flush(),
    setConsent: (patch) => consent.set(patch),
    getConsent: () => consent.get(),
    getInstallId: () => installId.peek(),
    getPlatform: () => platformInfo,
    showSmartBanner,
    hideSmartBanner,
    destroy() {
      destroyed = true;
      unsubscribe();
      queue.destroy();
      hideSmartBanner();
    },
  };

  if (config.smartBanner !== undefined) {
    // `document.body` is null while a script in <head> runs; lib.dom types it as always present.
    const doc = (globalThis as { document?: { body: HTMLElement | null } & Pick<Document, 'addEventListener'> })
      .document;
    if (doc?.body === null) {
      doc.addEventListener('DOMContentLoaded', () => showSmartBanner(), { once: true });
    } else {
      showSmartBanner();
    }
  }

  if (config.autoResolve === true) {
    resolve().catch((error: unknown) => {
      logger.warn('auto resolve failed', error instanceof Error ? error.message : error);
    });
  }

  return client;
}
