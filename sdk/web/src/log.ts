import type { LogLevel } from './types.js';

const ORDER: readonly LogLevel[] = ['silent', 'error', 'warn', 'info', 'debug'];

export interface Logger {
  readonly level: LogLevel;
  error(...args: unknown[]): void;
  warn(...args: unknown[]): void;
  info(...args: unknown[]): void;
  debug(...args: unknown[]): void;
  /** Derive a logger with a narrower prefix, e.g. `[dle:queue]`. */
  child(scope: string): Logger;
}

/**
 * Shorten a value so it can appear in a log line without disclosing it.
 *
 * The SDK never logs the SDK key, an `Authorization` header, a full referrer or a full
 * query string — that is a hard rule on the server side (SHARED-KERNEL §17.5) and there is
 * no reason for the client to be laxer. Install ids are shortened for the same reason:
 * a support engineer needs to correlate, not to re-identify.
 */
export function redact(value: string | null | undefined, keep = 6): string {
  if (value === null || value === undefined || value === '') return '<empty>';
  if (value.length <= keep) return `<redacted:${value.length}>`;
  return `${value.slice(0, keep)}…<redacted:${value.length}>`;
}

type Method = 'error' | 'warn' | 'info' | 'debug';

export function createLogger(level: LogLevel = 'warn', prefix = '[dle]'): Logger {
  const threshold = ORDER.indexOf(level);
  const emit =
    (method: Method) =>
    (...args: unknown[]): void => {
      if (ORDER.indexOf(method) > threshold) return;
      const c = (globalThis as { console?: Partial<Record<Method, (...a: unknown[]) => void>> }).console;
      c?.[method]?.(prefix, ...args);
    };
  return {
    level,
    error: emit('error'),
    warn: emit('warn'),
    info: emit('info'),
    debug: emit('debug'),
    child: (scope) => createLogger(level, `${prefix.slice(0, -1)}:${scope}]`),
  };
}
