import { useCallback, useEffect, useRef, useState, type DependencyList } from 'react';

export interface AsyncState<T> {
  data: T | undefined;
  error: unknown;
  loading: boolean;
  /** Re-runs the loader. Keeps the previous data visible while the new call is in flight. */
  reload: () => void;
  /** Replaces the data locally, for optimistic updates after a write. */
  setData: (updater: T | ((previous: T | undefined) => T | undefined)) => void;
}

/**
 * Minimal data hook: runs the loader when `deps` change, cancels the previous call through an
 * AbortSignal, and never applies a stale result. No cache; the console prefers a fresh read after
 * a write over a stale one.
 */
export function useAsync<T>(loader: (signal: AbortSignal) => Promise<T>, deps: DependencyList, enabled = true): AsyncState<T> {
  const [data, setData] = useState<T | undefined>(undefined);
  const [error, setError] = useState<unknown>(undefined);
  const [loading, setLoading] = useState<boolean>(enabled);
  const [tick, setTick] = useState(0);
  const controllerRef = useRef<AbortController | null>(null);

  useEffect(() => {
    if (!enabled) {
      setLoading(false);
      return;
    }
    controllerRef.current?.abort();
    const controller = new AbortController();
    controllerRef.current = controller;
    setLoading(true);
    setError(undefined);

    loader(controller.signal)
      .then((result) => {
        if (!controller.signal.aborted) {
          setData(result);
          setLoading(false);
        }
      })
      .catch((err: unknown) => {
        if (controller.signal.aborted) {
          return;
        }
        if (err instanceof DOMException && err.name === 'AbortError') {
          return;
        }
        setError(err);
        setLoading(false);
      });

    return () => controller.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps, tick, enabled]);

  const reload = useCallback(() => setTick((n) => n + 1), []);

  const set = useCallback((updater: T | ((previous: T | undefined) => T | undefined)) => {
    setData((previous) =>
      typeof updater === 'function' ? (updater as (p: T | undefined) => T | undefined)(previous) : updater,
    );
  }, []);

  return { data, error, loading, reload, setData: set };
}
