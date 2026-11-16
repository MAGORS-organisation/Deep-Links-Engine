const LOOPBACK_HOSTS = new Set(['localhost', '127.0.0.1', '[::1]', '::1']);

/**
 * Validate a configured HTTP(S) URL.
 *
 * Accepts `https:` URLs without embedded credentials. `http:` is permitted only for loopback hosts
 * in local development unless `allowInsecureHttp` is explicitly set to `true`.
 */
export function isSafeWebUrl(raw: string, allowInsecureHttp = false): boolean {
  if (typeof raw !== 'string' || raw === '' || /\s/.test(raw)) return false;
  let parsed: URL;
  try {
    parsed = new URL(raw);
  } catch {
    return false;
  }
  if (parsed.username !== '' || parsed.password !== '' || parsed.hostname === '') return false;
  if (parsed.protocol === 'https:') return true;
  if (parsed.protocol === 'http:') {
    return allowInsecureHttp || LOOPBACK_HOSTS.has(parsed.hostname.toLowerCase());
  }
  return false;
}
