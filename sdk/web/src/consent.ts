import type { DleConsent } from './types.js';
import type { KeyValueStore } from './storage.js';
import { readJson, writeJson } from './storage.js';
import type { Logger } from './log.js';

/** Storage key, namespaced by {@link createStore}. */
export const CONSENT_KEY = 'consent';

/** Consent-first default: nothing is permitted until the host site says so. */
export const DENIED: DleConsent = { analytics: false, attribution: false };

/** Wire shape of `ConsentDto` (SHARED-KERNEL §14). */
export interface ConsentWire {
  readonly analytics: boolean;
  readonly attribution: boolean;
  readonly ts?: string;
}

export interface ConsentManager {
  /** Current consent state. */
  get(): DleConsent;
  /** Merge a partial update, stamp it, persist it and notify listeners. */
  set(patch: Partial<DleConsent>): DleConsent;
  /** `true` when behavioural events may be reported. */
  canSendEvents(): boolean;
  /** `true` when a persistent identifier may be stored. */
  canPersistInstallId(): boolean;
  /** `true` when link parameters may be read from the URL and sent. */
  canReadLinkParams(): boolean;
  /**
   * `true` only when the operator opted in to probabilistic matching **and** the user
   * granted attribution consent. Both must hold — probabilistic matching is opt-in, off by
   * default and consent-gated (spec §0.2, §A.6, §E.6.2).
   */
  canSendDeviceSignals(probabilisticOptIn: boolean): boolean;
  /** Serialise for `ResolveRequestDto.consent`, or `undefined` when nothing was recorded. */
  toWire(): ConsentWire | undefined;
  /** Subscribe to changes. Returns an unsubscribe function. */
  onChange(listener: (consent: DleConsent) => void): () => void;
}

export interface ConsentOptions {
  readonly initial?: Partial<DleConsent> | undefined;
  readonly store: KeyValueStore;
  readonly logger?: Logger;
  readonly now?: () => Date;
}

function normalise(value: Partial<DleConsent> | null | undefined, ts?: string): DleConsent {
  const analytics = value?.analytics === true;
  const attribution = value?.attribution === true;
  const stamp = value?.ts ?? ts;
  return stamp === undefined ? { analytics, attribution } : { analytics, attribution, ts: stamp };
}

export function createConsent(options: ConsentOptions): ConsentManager {
  const { store, logger } = options;
  const now = options.now ?? ((): Date => new Date());
  const listeners = new Set<(consent: DleConsent) => void>();

  // A previously recorded decision wins over the config default: the user's last explicit
  // answer must survive a page reload, otherwise every navigation silently re-denies (or
  // worse, re-grants) consent. The stored record holds no identifier — only two booleans
  // and a timestamp — so keeping it is itself consistent with the "no persistent id
  // without consent" rule.
  const stored = readJson<Partial<DleConsent>>(store, CONSENT_KEY);
  let state: DleConsent = stored !== null ? normalise(stored) : normalise(options.initial);

  const notify = (): void => {
    for (const listener of listeners) {
      try {
        listener(state);
      } catch (error) {
        logger?.error('consent listener threw', error);
      }
    }
  };

  return {
    get: () => state,

    set(patch: Partial<DleConsent>): DleConsent {
      const next = normalise(
        {
          analytics: patch.analytics ?? state.analytics,
          attribution: patch.attribution ?? state.attribution,
          ts: patch.ts,
        },
        now().toISOString(),
      );
      const changed = next.analytics !== state.analytics || next.attribution !== state.attribution;
      state = next;
      writeJson(store, CONSENT_KEY, state);
      logger?.debug('consent set', { analytics: state.analytics, attribution: state.attribution });
      if (changed) notify();
      return state;
    },

    canSendEvents: () => state.analytics,
    canPersistInstallId: () => state.attribution,
    canReadLinkParams: () => state.attribution,
    canSendDeviceSignals: (probabilisticOptIn: boolean) => probabilisticOptIn && state.attribution,

    toWire(): ConsentWire | undefined {
      if (state.ts === undefined && !state.analytics && !state.attribution) {
        // Nothing was ever recorded. Sending `{false,false}` would be indistinguishable
        // from an explicit refusal; the engine must be able to tell the two apart.
        return undefined;
      }
      return state.ts === undefined
        ? { analytics: state.analytics, attribution: state.attribution }
        : { analytics: state.analytics, attribution: state.attribution, ts: state.ts };
    },

    onChange(listener) {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },
  };
}
