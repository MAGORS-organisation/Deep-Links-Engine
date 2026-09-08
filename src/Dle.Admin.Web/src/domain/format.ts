import { ApiError } from '../api/client';
import type { LinkResponse } from '../api/types';
import type { Translate } from '../i18n';

export type LinkStatus = 'active' | 'inactive' | 'quarantined' | 'expired' | 'scheduled';

export function linkStatus(link: LinkResponse, now = Date.now()): LinkStatus {
  if (link.quarantined_at) {
    return 'quarantined';
  }
  if (!link.is_active) {
    return 'inactive';
  }
  if (link.expires_at && Date.parse(link.expires_at) <= now) {
    return 'expired';
  }
  if (link.starts_at && Date.parse(link.starts_at) > now) {
    return 'scheduled';
  }
  return 'active';
}

/** ISO instant → value for <input type="datetime-local"> in the viewer's zone. */
export function toLocalInput(iso: string | null | undefined): string {
  if (!iso) {
    return '';
  }
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) {
    return '';
  }
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

/** datetime-local value → ISO instant in UTC, or null when empty. */
export function fromLocalInput(value: string): string | null {
  if (!value) {
    return null;
  }
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : date.toISOString();
}

export function parseList(text: string): string[] {
  return text
    .split(/[,\n]/)
    .map((s) => s.trim())
    .filter(Boolean);
}

export function parseIntList(text: string): number[] {
  return parseList(text)
    .map((s) => Number.parseInt(s, 10))
    .filter((n) => !Number.isNaN(n));
}

/** Human explanation of a failed call, in the current language. */
export function describeError(t: Translate, error: unknown): string {
  if (error instanceof ApiError) {
    switch (error.key) {
      case 'network':
        return t('error.network');
      case 'no_credential':
        return t('error.noCredential');
      case 'Unauthorized':
        return t('error.unauthorized');
      case 'Forbidden':
        return t('error.forbidden');
      case 'RateLimited':
        return t('error.rateLimited', { seconds: error.retryAfterSeconds ?? 60 });
      case 'SlugTaken':
        return t('error.slugTaken');
      case 'SlugInvalid':
        return t('error.slugInvalid');
      case 'UnsafeTarget':
        return error.detail ?? t('error.unsafeTarget');
      case 'MissingDefaultRule':
        return t('error.missingDefaultRule');
      case 'InvalidRoutingRules':
        return t('error.invalidRoutingRules');
      case 'DomainNotVerified':
        return t('error.domainNotVerified');
      case 'DomainTaken':
        return t('error.domainTaken');
      case 'IdempotencyConflict':
        return t('error.idempotencyConflict');
      case 'LinkQuarantined':
        return t('error.linkQuarantined');
      case 'DependencyUnavailable':
        return t('error.dependencyUnavailable');
      case 'ValidationFailed':
        return error.detail ?? t('error.validation');
      default:
        if (error.status === 401) return t('error.unauthorized');
        if (error.status === 403) return t('error.forbidden');
        if (error.status === 404) return error.detail ?? t('error.notFound');
        return error.detail ?? error.title ?? error.message;
    }
  }
  if (error instanceof Error) {
    return error.message;
  }
  return t('state.unexpected');
}

export function shortId(id: string, keep = 8): string {
  return id.length <= keep * 2 ? id : `${id.slice(0, keep)}…${id.slice(-4)}`;
}

export function hostOf(url: string): string {
  try {
    return new URL(url).host;
  } catch {
    return url;
  }
}

export function appendUtm(target: string, utm: Record<string, string>): string {
  try {
    const url = new URL(target);
    for (const [k, v] of Object.entries(utm)) {
      if (k.trim() && v.trim()) {
        url.searchParams.set(k.trim(), v.trim());
      }
    }
    return url.toString();
  } catch {
    return target;
  }
}
