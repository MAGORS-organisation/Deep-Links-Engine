import type { ProblemDocument } from './types';
import { problemKeyOf, type ProblemKey } from './problems';

/**
 * Where the console talks to and with what. Kept in session storage by default so a credential
 * does not outlive the tab; "remember on this device" moves it to local storage, which is the
 * operator's explicit choice (§E.4.1 K5: keys are replaceable, never recoverable).
 */
export interface Connection {
  baseUrl: string;
  apiKey: string;
  remember: boolean;
}

const STORAGE_KEY = 'dle.admin.connection';

const listeners = new Set<() => void>();

function readStore(storage: Storage): Connection | null {
  try {
    const raw = storage.getItem(STORAGE_KEY);
    if (!raw) {
      return null;
    }
    const parsed = JSON.parse(raw) as Partial<Connection>;
    if (typeof parsed.apiKey !== 'string') {
      return null;
    }
    return {
      baseUrl: typeof parsed.baseUrl === 'string' ? parsed.baseUrl : '',
      apiKey: parsed.apiKey,
      remember: storage === localStorage,
    };
  } catch {
    return null;
  }
}

let cached: Connection | null | undefined;

export function getConnection(): Connection | null {
  if (cached === undefined) {
    cached = readStore(sessionStorage) ?? readStore(localStorage);
  }
  return cached;
}

export function setConnection(next: Connection | null): void {
  try {
    sessionStorage.removeItem(STORAGE_KEY);
    localStorage.removeItem(STORAGE_KEY);
    if (next) {
      const target = next.remember ? localStorage : sessionStorage;
      target.setItem(
        STORAGE_KEY,
        JSON.stringify({ baseUrl: next.baseUrl.replace(/\/+$/, ''), apiKey: next.apiKey.trim() }),
      );
    }
  } catch {
    // Storage may be unavailable (private mode, blocked). The in-memory copy still works for the tab.
  }
  cached = next
    ? { ...next, baseUrl: next.baseUrl.replace(/\/+$/, ''), apiKey: next.apiKey.trim() }
    : null;
  listeners.forEach((l) => l());
}

export function subscribeConnection(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

/**
 * A failed call. `key` is the symbolic problem code so callers can branch without comparing
 * URIs; `errors` carries the per-field messages of a validation problem, keyed by the wire path
 * the server used (for example `rules[2].then.url` or `target_url`).
 */
export class ApiError extends Error {
  readonly status: number;
  readonly type: string | undefined;
  readonly title: string | undefined;
  readonly detail: string | undefined;
  readonly errors: Record<string, string[]>;
  readonly key: ProblemKey;
  readonly retryAfterSeconds: number | undefined;

  constructor(init: {
    status: number;
    problem?: ProblemDocument | undefined;
    key?: ProblemKey;
    message?: string;
    retryAfterSeconds?: number;
  }) {
    const problem = init.problem;
    super(init.message ?? problem?.detail ?? problem?.title ?? `HTTP ${init.status}`);
    this.name = 'ApiError';
    this.status = init.status;
    this.type = problem?.type;
    this.title = problem?.title;
    this.detail = problem?.detail;
    this.errors = problem?.errors ?? {};
    this.key = init.key ?? problemKeyOf(problem?.type);
    this.retryAfterSeconds = init.retryAfterSeconds;
  }

  /** First message recorded for a field, if the server reported one. */
  fieldError(path: string): string | undefined {
    return this.errors[path]?.[0];
  }

  get isUnauthorized(): boolean {
    return this.status === 401 || this.key === 'Unauthorized' || this.key === 'no_credential';
  }

  get isForbidden(): boolean {
    return this.status === 403 || this.key === 'Forbidden';
  }
}

export function isApiError(value: unknown): value is ApiError {
  return value instanceof ApiError;
}

type QueryValue = string | number | boolean | null | undefined;

export interface RequestOptions {
  method?: 'GET' | 'POST' | 'PATCH' | 'PUT' | 'DELETE';
  query?: Record<string, QueryValue>;
  body?: unknown;
  /** Adds an Idempotency-Key. Defaults to true for every non-GET request. */
  idempotent?: boolean;
  headers?: Record<string, string>;
  signal?: AbortSignal;
  /** Accept-Language override, used by the simulator to fetch a Slovak explanation. */
  language?: string;
}

function buildQuery(query: Record<string, QueryValue> | undefined): string {
  if (!query) {
    return '';
  }
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value === undefined || value === null || value === '') {
      continue;
    }
    params.set(key, String(value));
  }
  const text = params.toString();
  return text ? `?${text}` : '';
}

function newIdempotencyKey(): string {
  // lib.dom declares randomUUID as always present, which narrows `crypto` to `never` after the
  // guard and breaks the fallback. Widen the view so the guard is honest about engines without it.
  const c = globalThis.crypto as Crypto & { randomUUID?: () => string };
  if (typeof c.randomUUID === 'function') {
    return c.randomUUID();
  }
  // Fallback for very old engines; still 128 bits of randomness.
  const bytes = new Uint8Array(16);
  c.getRandomValues(bytes);
  return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
}

async function readProblem(response: Response): Promise<ProblemDocument | undefined> {
  const contentType = response.headers.get('content-type') ?? '';
  if (!contentType.includes('json')) {
    return undefined;
  }
  try {
    return (await response.json()) as ProblemDocument;
  } catch {
    return undefined;
  }
}

/**
 * Performs one request against the control plane. Throws ApiError for anything that is not a
 * 2xx, including a missing credential and network failure, so a page has one error path.
 */
export async function api<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const connection = getConnection();
  if (!connection) {
    throw new ApiError({ status: 0, key: 'no_credential', message: 'No API key configured.' });
  }

  const method = options.method ?? 'GET';
  const headers: Record<string, string> = {
    Accept: 'application/json, application/problem+json',
    Authorization: `Bearer ${connection.apiKey}`,
    ...options.headers,
  };

  if (options.language) {
    headers['Accept-Language'] = options.language;
  }

  let body: string | undefined;
  if (options.body !== undefined) {
    headers['Content-Type'] = 'application/json';
    body = JSON.stringify(options.body);
  }

  const idempotent = options.idempotent ?? method !== 'GET';
  if (idempotent) {
    headers['Idempotency-Key'] = newIdempotencyKey();
  }

  const url = `${connection.baseUrl}${path}${buildQuery(options.query)}`;

  let response: Response;
  try {
    response = await fetch(url, {
      method,
      headers,
      body,
      signal: options.signal,
      credentials: 'omit',
      referrerPolicy: 'no-referrer',
    });
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') {
      throw error;
    }
    throw new ApiError({ status: 0, key: 'network', message: 'The control plane could not be reached.' });
  }

  if (!response.ok) {
    const problem = await readProblem(response);
    const retryAfter = response.headers.get('retry-after');
    throw new ApiError({
      status: response.status,
      problem,
      retryAfterSeconds: retryAfter ? Number(retryAfter) || undefined : undefined,
    });
  }

  if (response.status === 204 || response.headers.get('content-length') === '0') {
    return undefined as T;
  }

  const contentType = response.headers.get('content-type') ?? '';
  if (contentType.includes('json')) {
    return (await response.json()) as T;
  }

  const text = await response.text();
  if (!text) {
    return undefined as T;
  }
  throw new ApiError({
    status: response.status,
    key: 'unexpected_body',
    message: 'The control plane answered with something that is not JSON.',
  });
}
