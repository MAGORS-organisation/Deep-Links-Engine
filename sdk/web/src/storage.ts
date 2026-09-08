/**
 * Namespaced, failure-tolerant key/value storage.
 *
 * Design constraints:
 *  - **Cookie-less.** The SDK never reads or writes `document.cookie`. Cookies travel to
 *    the server on every request and would turn a first-party convenience into a tracking
 *    surface the operator did not ask for.
 *  - **Never throws.** Safari in private mode, storage-partitioned third-party frames and
 *    "block all cookies" settings all make `localStorage` access throw. Every accessor is
 *    wrapped and degrades to an in-memory map that lives only for this page view.
 *  - **No fallback identifiers.** When persistence is unavailable the SDK loses continuity.
 *    It does not reach for IndexedDB, cache probes, ETags or anything else that would
 *    reconstruct an identifier behind the user's back (FR-227).
 */

export interface KeyValueStore {
  get(key: string): string | null;
  set(key: string, value: string): void;
  remove(key: string): void;
  /** `false` when the backing store is the in-memory fallback. */
  readonly persistent: boolean;
}

type WebStorageLike = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>;

function probe(candidate: WebStorageLike | null | undefined): WebStorageLike | null {
  if (candidate === null || candidate === undefined) return null;
  // A read/write round trip is the only reliable probe: Safari private mode exposes the
  // object and throws on write, Firefox with storage disabled throws on read.
  const probeKey = '__dle_probe__';
  try {
    candidate.setItem(probeKey, '1');
    candidate.removeItem(probeKey);
    return candidate;
  } catch {
    return null;
  }
}

function memoryStore(namespace: string): KeyValueStore {
  const map = new Map<string, string>();
  return {
    persistent: false,
    get: (key) => map.get(`${namespace}.${key}`) ?? null,
    set: (key, value) => {
      map.set(`${namespace}.${key}`, value);
    },
    remove: (key) => {
      map.delete(`${namespace}.${key}`);
    },
  };
}

function wrap(backing: WebStorageLike, namespace: string): KeyValueStore {
  const fullKey = (key: string): string => `${namespace}.${key}`;
  const fallback = memoryStore(namespace);
  return {
    persistent: true,
    get: (key) => {
      try {
        return backing.getItem(fullKey(key));
      } catch {
        return fallback.get(key);
      }
    },
    set: (key, value) => {
      try {
        backing.setItem(fullKey(key), value);
      } catch {
        // Quota exceeded mid-session, or storage revoked after the probe succeeded.
        fallback.set(key, value);
      }
    },
    remove: (key) => {
      try {
        backing.removeItem(fullKey(key));
      } catch {
        /* nothing to do: the value is already unreachable */
      }
      fallback.remove(key);
    },
  };
}

function pick(kind: 'local' | 'session'): WebStorageLike | null {
  const scope = globalThis as { localStorage?: Storage; sessionStorage?: Storage };
  try {
    return probe(kind === 'local' ? scope.localStorage : scope.sessionStorage);
  } catch {
    // Merely touching the property throws in some hardened configurations.
    return null;
  }
}

/** Durable store backed by `localStorage`, with an in-memory fallback. */
export function createStore(namespace = 'dle', backing?: WebStorageLike | null): KeyValueStore {
  const resolved = backing === undefined ? pick('local') : probe(backing);
  return resolved === null ? memoryStore(namespace) : wrap(resolved, namespace);
}

/** Per-tab store backed by `sessionStorage`, with an in-memory fallback. */
export function createSessionStore(
  namespace = 'dle',
  backing?: WebStorageLike | null,
): KeyValueStore {
  const resolved = backing === undefined ? pick('session') : probe(backing);
  return resolved === null ? memoryStore(namespace) : wrap(resolved, namespace);
}

/** Exported for tests and for hosts that need an explicit non-persistent store. */
export function createMemoryStore(namespace = 'dle'): KeyValueStore {
  return memoryStore(namespace);
}

/** Read and JSON-parse a key, returning `null` for missing or corrupt values. */
// The type parameter names what the caller stored; parsed JSON is trusted only as far as the
// caller's own subsequent checks, which is why it is a parameter and not a return of `unknown`.
// eslint-disable-next-line @typescript-eslint/no-unnecessary-type-parameters
export function readJson<T>(store: KeyValueStore, key: string): T | null {
  const raw = store.get(key);
  if (raw === null) return null;
  try {
    return JSON.parse(raw) as T;
  } catch {
    store.remove(key);
    return null;
  }
}

/** JSON-serialise and write a key. Silently gives up when serialisation fails. */
export function writeJson(store: KeyValueStore, key: string, value: unknown): void {
  try {
    store.set(key, JSON.stringify(value));
  } catch {
    /* circular structure or storage failure — the caller keeps its in-memory copy */
  }
}
