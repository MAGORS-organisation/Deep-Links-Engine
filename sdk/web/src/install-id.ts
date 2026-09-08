import type { Logger } from './log.js';
import { redact } from './log.js';
import { cryptoObject, hex, randomBytes } from './random.js';
import type { KeyValueStore } from './storage.js';

/** Storage key, namespaced by {@link createStore}. */
export const INSTALL_ID_KEY = 'install_id';

const UUID_V4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

/**
 * Generate a random UUID v4.
 *
 * This is the only identifier the Web SDK ever creates and it is **random**, never derived.
 * If storage is cleared the user is a new user; that is the intended behaviour, not a defect
 * to be engineered around (FR-227, spec §0.2).
 */
export function generateInstallId(): string {
  const c = cryptoObject();
  if (typeof c?.randomUUID === 'function') {
    try {
      const value = c.randomUUID();
      if (UUID_V4.test(value)) return value;
    } catch {
      /* fall through */
    }
  }
  const b = randomBytes(16);
  // RFC 9562 §5.4: version 4, variant 10xx.
  b[6] = ((b[6] ?? 0) & 0x0f) | 0x40;
  b[8] = ((b[8] ?? 0) & 0x3f) | 0x80;
  const s = hex(b);
  return `${s.slice(0, 8)}-${s.slice(8, 12)}-${s.slice(12, 16)}-${s.slice(16, 20)}-${s.slice(20)}`;
}

export interface InstallIdManager {
  /** Current id, creating one when needed. */
  get(): string;
  /** Current id without creating one. */
  peek(): string | null;
  /** Forget the id, in storage and in memory. Called when attribution consent is revoked. */
  clear(): void;
  /** Re-evaluate persistence after a consent change. */
  reconcile(): void;
  /** `true` when the current id is written to durable storage. */
  readonly persisted: boolean;
}

export interface InstallIdOptions {
  readonly store: KeyValueStore;
  /**
   * Consent gate. While this returns `false` the id is kept **in memory only** and any
   * previously stored id is deleted: no persistent identifier without attribution consent
   * (spec §E.6.2, TC-145/TC-146).
   */
  readonly mayPersist: () => boolean;
  readonly logger?: Logger | undefined;
}

export function createInstallId(options: InstallIdOptions): InstallIdManager {
  const { store, mayPersist, logger } = options;
  let memory: string | null = null;
  let persisted = false;

  const readStored = (): string | null => {
    const raw = store.get(INSTALL_ID_KEY);
    return raw !== null && UUID_V4.test(raw) ? raw : null;
  };

  const dropStored = (): void => {
    if (readStored() !== null) {
      store.remove(INSTALL_ID_KEY);
      logger?.debug('install id removed from storage (no attribution consent)');
    }
    persisted = false;
  };

  const write = (id: string): void => {
    store.set(INSTALL_ID_KEY, id);
    persisted = store.persistent && readStored() !== null;
  };

  return {
    get persisted() {
      return persisted;
    },

    peek(): string | null {
      if (!mayPersist()) return memory;
      return memory ?? readStored();
    },

    get(): string {
      if (!mayPersist()) {
        dropStored();
        memory ??= generateInstallId();
        return memory;
      }
      const stored = readStored();
      if (stored !== null) {
        memory = stored;
        persisted = store.persistent;
        return stored;
      }
      memory ??= generateInstallId();
      write(memory);
      logger?.debug('install id created', redact(memory, 8), persisted ? '(persisted)' : '(memory only)');
      return memory;
    },

    clear(): void {
      dropStored();
      memory = null;
    },

    reconcile(): void {
      if (!mayPersist()) {
        dropStored();
        return;
      }
      if (memory !== null && readStored() === null) write(memory);
    },
  };
}
