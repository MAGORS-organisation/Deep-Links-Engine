import type { PillTone } from './StatusPill';

/**
 * Maps a verification status string from the API (`ok`, `failed`, `warning`, `pending`) to a
 * pill tone. Lives outside `StatusPill.tsx` so that file exports only components, which is what
 * keeps React Fast Refresh working for it.
 */
export function toneOfVerification(status: string | null | undefined): PillTone {
  switch (status) {
    case 'ok':
      return 'ok';
    case 'failed':
      return 'bad';
    case 'warning':
      return 'warn';
    case 'pending':
      return 'pending';
    default:
      return 'neutral';
  }
}
