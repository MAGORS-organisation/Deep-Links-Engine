import {
  DleError,
  buildEventBatch,
  buildResolveRequest,
  collectSignals,
  createApiClient,
  parseResolveResponse,
} from './api.js';
import { createConsent } from './consent.js';
import { createInstallId } from './install-id.js';
import { createLogger } from './log.js';
import { detect } from './platform.js';
import { createQueue, isValidEvent } from './queue.js';
import { createSmartBanner } from './smart-banner.js';
import { createSessionStore, createStore, readJson, writeJson } from './storage.js';
import type {
  DevicePlatform,
  DleClient,
  DleConfig,
  DleEvent,
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
export const VERSION = '0.1.0';

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
  if (typeof config.endpoint !== 'string' || !/^https?:\/\/[^\s/]+/i.test(config.endpoint)) {
    throw new DleError('config', 'endpoint must be an absolute http(s) URL');
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

  const unsubscribe = consent.onChange((state) => {
    installId.reconcile();
    if (!state.analytics) queue.clear();
    if (!state.attribution) {
      installId.clear();
      session.remove(RESOLVE_CACHE_KEY);
    }
  });

  const readCache = (id: string): ResolveResult | null => {
    const entry = readJson<ResolveCacheEntry>(session, RESOLVE_CACHE_KEY);
    if (entry?.install_id !== id) return null;
    const { expiresIn } = entry.result;
    if (expiresIn > 0 && entry.stored_at + expiresIn * 1000 <= Date.now()) {
      session.remove(RESOLVE_CACHE_KEY);
      return null;
    }
    return entry.result;
  };

  async function resolve(options: ResolveOptions = {}): Promise<ResolveResult> {
    const id = installId.get();
    if (options.force !== true) {
      const cached = readCache(id);
      if (cached !== null) return cached;
    }

    const attribution = consent.canReadLinkParams();
    const search = (globalThis as { location?: { search?: string } }).location?.search;
    const referrer =
      options.referrer ??
      (attribution && config.readUrlParams !== false && typeof search === 'string'
        ? referrerFromSearch(search)
        : undefined);

    // Consent false: no `signals` member at all — not an empty object (TC-145, TC-146).
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

    const result = parseResolveResponse(await api.resolve(body));
    writeJson(session, RESOLVE_CACHE_KEY, { install_id: id, stored_at: Date.now(), result } satisfies ResolveCacheEntry);
    logger.debug('resolved', result.matchType, result.confidence);
    return result;
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
