import { DleError, MAX_EVENTS_PER_BATCH } from './api.js';
import type { Logger } from './log.js';
import type { KeyValueStore } from './storage.js';
import { readJson, writeJson } from './storage.js';
import type { DleEvent, DleEventType } from './types.js';

/**
 * Offline event queue with persistence and retry (FR-226, spec §C.5).
 *
 * - Events are appended to a durable, namespaced store and survive reloads and offline spells.
 * - Flushes are debounced and batched, at most `MAX_EVENTS_PER_BATCH` (100) per request, so a
 *   burst of events costs one request rather than one each.
 * - Retriable failures (network, timeout, 408, 429, 5xx) keep the events and back off
 *   exponentially, honouring `Retry-After`. Non-retriable failures (400, 401, 403, 413) drop
 *   the batch: a body the server refuses today it will refuse tomorrow.
 * - On `pagehide` / hidden tab the queue sends one batch with `fetch` `keepalive`, which is
 *   the only unload-safe transport that can carry the `Authorization` header. The batch is
 *   removed optimistically, because no acknowledgement can be awaited past unload and a
 *   duplicate would inflate counts. Anything left goes out on the next page load.
 * - The queue holds events only, never an identifier. `install_id` is attached at send time.
 * - Events older than `maxAgeMs` (30 days, `RecordEvents.PastTolerance`) are pruned: the
 *   server would reject them and they would only spend rate-limit budget.
 */

/** Storage key, namespaced by {@link createStore}. */
export const QUEUE_KEY = 'queue';

/** Mirrors `RecordEvents.PastTolerance` on the server. */
export const DEFAULT_MAX_EVENT_AGE_MS = 30 * 24 * 60 * 60 * 1000;

const MAX_BACKOFF_MS = 5 * 60 * 1000;

const EVENT_TYPES: readonly DleEventType[] = ['link_open', 'first_open', 'session', 'conversion', 'custom'];

/** `true` when the value is a well-formed event the server can accept. */
export function isValidEvent(value: unknown): value is DleEvent {
  if (typeof value !== 'object' || value === null) return false;
  const type = (value as { type?: unknown }).type;
  return typeof type === 'string' && (EVENT_TYPES as readonly string[]).includes(type);
}

interface ListenerTarget {
  addEventListener(type: string, listener: () => void): void;
  removeEventListener(type: string, listener: () => void): void;
}

export interface QueueOptions {
  readonly store: KeyValueStore;
  /** Transport. Throws {@link DleError}; `retriable` decides whether the batch is kept. */
  readonly send: (events: readonly DleEvent[], keepalive: boolean) => Promise<void>;
  /** Consent gate. When it returns `false` the queue is discarded instead of flushed. */
  readonly mayFlush: () => boolean;
  readonly maxSize?: number | undefined;
  readonly flushIntervalMs?: number | undefined;
  readonly maxAgeMs?: number | undefined;
  readonly logger?: Logger | undefined;
  readonly now?: (() => number) | undefined;
  /** `window`, for `pagehide` and `online`. `null` disables the listeners. */
  readonly window?: (ListenerTarget & { navigator?: { onLine?: boolean } }) | null | undefined;
  /** `document`, for `visibilitychange`. `null` disables the listener. */
  readonly document?: (ListenerTarget & { visibilityState?: string }) | null | undefined;
}

export interface EventQueue {
  /** Append an event (timestamped when it has no `ts`) and schedule a flush. */
  enqueue(event: DleEvent): void;
  /** Send what is queued. Resolves `true` when the queue is empty afterwards. */
  flush(options?: { readonly keepalive?: boolean }): Promise<boolean>;
  /** Number of queued events. */
  size(): number;
  /** Discard everything, in memory and in storage. */
  clear(): void;
  /** Stop timers and listeners. */
  destroy(): void;
}

function ageOf(event: DleEvent, now: number): number {
  if (event.ts === undefined) return 0;
  const at = Date.parse(event.ts);
  return Number.isNaN(at) ? Number.POSITIVE_INFINITY : now - at;
}

export function createQueue(options: QueueOptions): EventQueue {
  const { store, send, mayFlush, logger } = options;
  const maxSize = Math.max(1, options.maxSize ?? 200);
  const flushIntervalMs = Math.max(0, options.flushIntervalMs ?? 3000);
  const maxAgeMs = options.maxAgeMs ?? DEFAULT_MAX_EVENT_AGE_MS;
  const now = options.now ?? ((): number => Date.now());
  // Outside a browser (SSR, workers) there is no window to listen on; the queue still works.
  const g = globalThis as { addEventListener?: unknown; document?: QueueOptions['document'] };
  const win =
    options.window === undefined
      ? typeof g.addEventListener === 'function'
        ? (globalThis as NonNullable<QueueOptions['window']>)
        : null
      : options.window;
  const doc =
    options.document === undefined
      ? typeof g.document?.addEventListener === 'function'
        ? g.document
        : null
      : options.document;

  let items: DleEvent[] = [];
  let timer: ReturnType<typeof setTimeout> | null = null;
  let inflight: Promise<boolean> | null = null;
  let attempt = 0;
  let nextAttemptAt = 0;
  let destroyed = false;

  const persist = (): void => {
    if (items.length === 0) store.remove(QUEUE_KEY);
    else writeJson(store, QUEUE_KEY, items);
  };

  const load = (): void => {
    const stored = readJson<unknown>(store, QUEUE_KEY);
    if (!Array.isArray(stored)) return;
    const t = now();
    items = stored.filter((e): e is DleEvent => isValidEvent(e) && ageOf(e, t) <= maxAgeMs).slice(-maxSize);
    if (items.length !== stored.length) persist();
  };

  const schedule = (delay: number): void => {
    if (destroyed || timer !== null) return;
    timer = setTimeout(() => {
      timer = null;
      void flush();
    }, delay);
  };

  const remove = (batch: readonly DleEvent[]): void => {
    const sent = new Set<DleEvent>(batch);
    items = items.filter((e) => !sent.has(e));
    persist();
  };

  async function run(keepalive: boolean): Promise<boolean> {
    while (items.length > 0) {
      const batch = items.slice(0, MAX_EVENTS_PER_BATCH);
      if (keepalive) {
        // The page is going away: one keepalive batch (the browser caps in-flight keepalive
        // bodies at 64 KiB per origin), removed up front — see the module comment.
        remove(batch);
        void send(batch, true).catch((error: unknown) => {
          logger?.debug('keepalive flush failed', error instanceof Error ? error.message : error);
        });
        return items.length === 0;
      }
      try {
        await send(batch, false);
        remove(batch);
        attempt = 0;
        nextAttemptAt = 0;
      } catch (error) {
        if (error instanceof DleError && !error.retriable) {
          logger?.warn(`dropping ${batch.length} event(s): ${error.message}`, error.status ?? error.code);
          remove(batch);
          continue;
        }
        attempt += 1;
        const fromServer = error instanceof DleError ? error.retryAfterMs : undefined;
        const delay = fromServer ?? Math.min(MAX_BACKOFF_MS, 1000 * 2 ** (attempt - 1));
        nextAttemptAt = now() + delay;
        logger?.debug(`flush failed, retry in ${delay} ms`, error instanceof Error ? error.message : error);
        schedule(delay);
        return false;
      }
    }
    return true;
  }

  function flush(flushOptions: { readonly keepalive?: boolean } = {}): Promise<boolean> {
    if (!mayFlush()) {
      if (items.length > 0) {
        items = [];
        persist();
      }
      return Promise.resolve(true);
    }
    if (items.length === 0) return Promise.resolve(true);
    if (inflight !== null) return inflight;
    const t = now();
    if (t < nextAttemptAt) {
      schedule(nextAttemptAt - t);
      return Promise.resolve(false);
    }
    if (win?.navigator?.onLine === false) return Promise.resolve(false);
    inflight = run(flushOptions.keepalive === true).finally(() => {
      inflight = null;
    });
    return inflight;
  }

  const onHide = (): void => {
    void flush({ keepalive: true });
  };
  const onVisibility = (): void => {
    if (doc?.visibilityState === 'hidden') onHide();
  };
  const onOnline = (): void => {
    void flush();
  };

  win?.addEventListener('pagehide', onHide);
  win?.addEventListener('online', onOnline);
  doc?.addEventListener('visibilitychange', onVisibility);

  load();
  if (items.length > 0) schedule(flushIntervalMs);

  return {
    enqueue(event) {
      if (destroyed) return;
      const stamped: DleEvent = event.ts === undefined ? { ...event, ts: new Date(now()).toISOString() } : event;
      items.push(stamped);
      if (items.length > maxSize) {
        items.splice(0, items.length - maxSize);
        logger?.warn(`queue full (${maxSize}); oldest events dropped`);
      }
      persist();
      schedule(flushIntervalMs);
    },
    flush,
    size: () => items.length,
    clear() {
      items = [];
      persist();
    },
    destroy() {
      destroyed = true;
      if (timer !== null) {
        clearTimeout(timer);
        timer = null;
      }
      win?.removeEventListener('pagehide', onHide);
      win?.removeEventListener('online', onOnline);
      doc?.removeEventListener('visibilitychange', onVisibility);
    },
  };
}
