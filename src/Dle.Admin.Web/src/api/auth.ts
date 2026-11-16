export interface AuthConfig {
  oidc_enabled: boolean;
  authority: string | null;
  client_id: string | null;
  audience: string | null;
  scopes: string[];
}

const PENDING_OIDC_KEY = 'dle.oidc.pending';

interface PendingOidc {
  codeVerifier: string;
  state: string;
  authority: string;
  clientId: string;
  baseUrl: string;
  redirectUri: string;
}

/** Fetches public auth / OIDC configuration from the control plane. */
export async function getAuthConfig(baseUrl: string = ''): Promise<AuthConfig> {
  const url = `${baseUrl.replace(/\/+$/, '')}/api/v1/auth/config`;
  try {
    const res = await fetch(url, {
      headers: { Accept: 'application/json' },
    });
    if (!res.ok) {
      return { oidc_enabled: false, authority: null, client_id: null, audience: null, scopes: [] };
    }
    return (await res.json()) as AuthConfig;
  } catch {
    return { oidc_enabled: false, authority: null, client_id: null, audience: null, scopes: [] };
  }
}

function generateRandomString(length: number = 48): string {
  const charset = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~';
  const values = new Uint8Array(length);
  crypto.getRandomValues(values);
  return Array.from(values, (v) => charset[v % charset.length]).join('');
}

async function sha256(plain: string): Promise<ArrayBuffer> {
  const encoder = new TextEncoder();
  const data = encoder.encode(plain);
  return crypto.subtle.digest('SHA-256', data);
}

function base64UrlEncode(buffer: ArrayBuffer): string {
  const bytes = new Uint8Array(buffer);
  let binary = '';
  for (let i = 0; i < bytes.byteLength; i++) {
    binary += String.fromCharCode(bytes[i] ?? 0);
  }
  return btoa(binary)
    .replace(/\+/g, '-')
    .replace(/\//g, '_')
    .replace(/=+$/, '');
}

function isLoopbackHost(hostname: string): boolean {
  return hostname === 'localhost' || hostname === '127.0.0.1' || hostname === '[::1]';
}

function parseAuthorityUrl(rawAuthority: string): { url: URL; normalized: string } {
  let url: URL;
  try {
    url = new URL(rawAuthority.trim());
  } catch {
    throw new Error('Invalid OIDC authority URL.');
  }

  if (url.username || url.password || url.search || url.hash) {
    throw new Error('OIDC authority URL must not include credentials, query strings, or fragments.');
  }

  if (url.protocol !== 'https:' && !(url.protocol === 'http:' && isLoopbackHost(url.hostname))) {
    throw new Error('OIDC authority URL must use HTTPS (or HTTP only on loopback).');
  }

  const cleanPath = url.pathname.replace(/\/+$/, '');
  return { url, normalized: `${url.origin}${cleanPath}` };
}

function validateDiscoveredEndpoint(rawEndpoint: unknown, authority: { url: URL; normalized: string }, fieldName: string): string {
  if (typeof rawEndpoint !== 'string' || !rawEndpoint.trim()) {
    throw new Error(`OIDC discovery returned invalid ${fieldName}.`);
  }

  let endpoint: URL;
  try {
    endpoint = new URL(rawEndpoint);
  } catch {
    throw new Error(`OIDC discovery returned malformed ${fieldName}.`);
  }

  if (endpoint.username || endpoint.password) {
    throw new Error(`OIDC ${fieldName} must not contain embedded credentials.`);
  }

  if (endpoint.protocol !== 'https:' && !(endpoint.protocol === 'http:' && isLoopbackHost(endpoint.hostname))) {
    throw new Error(`OIDC ${fieldName} must use HTTPS.`);
  }

  if (endpoint.origin !== authority.url.origin) {
    throw new Error(`OIDC ${fieldName} origin (${endpoint.origin}) does not match authority origin (${authority.url.origin}).`);
  }

  return endpoint.toString();
}

function validateDiscoveryIssuer(issuer: unknown, authority: { url: URL; normalized: string }): void {
  if (issuer === undefined) {
    return;
  }
  if (typeof issuer !== 'string') {
    throw new Error('OIDC discovery issuer must be a string.');
  }
  const parsedIssuer = parseAuthorityUrl(issuer);
  if (parsedIssuer.normalized !== authority.normalized) {
    throw new Error('OIDC discovery issuer does not match the configured authority.');
  }
}

/** Initiates an interactive OAuth2/OIDC PKCE authorization flow. */
export async function initiateOidcLogin(
  baseUrl: string,
  authority: string,
  clientId: string,
  scopes: string[] = ['openid', 'profile', 'email'],
  audience: string | null = null,
): Promise<void> {
  const parsedAuthority = parseAuthorityUrl(authority);
  const normalizedAuthority = parsedAuthority.normalized;
  const codeVerifier = generateRandomString(64);
  const state = generateRandomString(32);
  const hashed = await sha256(codeVerifier);
  const codeChallenge = base64UrlEncode(hashed);

  const redirectUri = `${window.location.origin}${window.location.pathname}`;

  const pending: PendingOidc = {
    codeVerifier,
    state,
    authority: normalizedAuthority,
    clientId,
    baseUrl: baseUrl.replace(/\/+$/, ''),
    redirectUri,
  };

  sessionStorage.setItem(PENDING_OIDC_KEY, JSON.stringify(pending));

  // Try discovery endpoint, fallback to standard Keycloak/OIDC path
  let authEndpoint = `${normalizedAuthority}/protocol/openid-connect/auth`;
  let discoveryResponse: Response | null = null;
  try {
    discoveryResponse = await fetch(`${normalizedAuthority}/.well-known/openid-configuration`);
  } catch {
    // Keep same-origin fallback when discovery endpoint is unreachable
  }

  if (discoveryResponse?.ok) {
    const discoJson = (await discoveryResponse.json()) as {
      issuer?: unknown;
      authorization_endpoint?: unknown;
    };
    validateDiscoveryIssuer(discoJson.issuer, parsedAuthority);
    if (discoJson.authorization_endpoint !== undefined) {
      authEndpoint = validateDiscoveredEndpoint(
        discoJson.authorization_endpoint,
        parsedAuthority,
        'authorization_endpoint',
      );
    }
  }

  const params = new URLSearchParams({
    response_type: 'code',
    client_id: clientId,
    redirect_uri: redirectUri,
    scope: scopes.join(' '),
    state,
    code_challenge: codeChallenge,
    code_challenge_method: 'S256',
  });
  // Some providers (Auth0) issue an access token for the API only when the audience is requested.
  if (audience) {
    params.set('audience', audience);
  }

  window.location.assign(`${authEndpoint}?${params.toString()}`);
}

/** Handles return from OIDC provider if query params code and state are present. */
export async function handleOidcCallback(): Promise<{ token: string; baseUrl: string } | null> {
  const urlParams = new URLSearchParams(window.location.search);
  const code = urlParams.get('code');
  const state = urlParams.get('state');

  if (!code || !state) {
    return null;
  }

  const stored = sessionStorage.getItem(PENDING_OIDC_KEY);
  if (!stored) {
    return null;
  }

  sessionStorage.removeItem(PENDING_OIDC_KEY);

  // The code is single use: drop it from the address bar whether or not the exchange succeeds.
  const cleanUrl = new URL(window.location.href);
  cleanUrl.searchParams.delete('code');
  cleanUrl.searchParams.delete('state');
  cleanUrl.searchParams.delete('session_state');
  window.history.replaceState(window.history.state, '', cleanUrl.toString());

  let pending: PendingOidc;
  try {
    pending = JSON.parse(stored) as PendingOidc;
  } catch {
    return null;
  }

  if (state !== pending.state) {
    throw new Error('OIDC state parameter mismatch.');
  }

  const parsedAuthority = parseAuthorityUrl(pending.authority);

  if (!pending.redirectUri || new URL(pending.redirectUri).origin !== window.location.origin) {
    throw new Error('OIDC redirect URI origin mismatch.');
  }

  // Token endpoint discovery
  let tokenEndpoint = `${parsedAuthority.normalized}/protocol/openid-connect/token`;
  let discoveryResponse: Response | null = null;
  try {
    discoveryResponse = await fetch(`${parsedAuthority.normalized}/.well-known/openid-configuration`);
  } catch {
    // Keep same-origin fallback when discovery endpoint is unreachable
  }

  if (discoveryResponse?.ok) {
    const discoJson = (await discoveryResponse.json()) as {
      issuer?: unknown;
      token_endpoint?: unknown;
    };
    validateDiscoveryIssuer(discoJson.issuer, parsedAuthority);
    if (discoJson.token_endpoint !== undefined) {
      tokenEndpoint = validateDiscoveredEndpoint(
        discoJson.token_endpoint,
        parsedAuthority,
        'token_endpoint',
      );
    }
  }

  const body = new URLSearchParams({
    grant_type: 'authorization_code',
    client_id: pending.clientId,
    code,
    redirect_uri: pending.redirectUri,
    code_verifier: pending.codeVerifier,
  });

  const response = await fetch(tokenEndpoint, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/x-www-form-urlencoded',
      Accept: 'application/json',
    },
    body: body.toString(),
  });

  if (!response.ok) {
    const errText = await response.text();
    throw new Error(`OIDC token exchange failed: HTTP ${response.status} ${errText}`);
  }

  // Only the access token is meant for the API; an ID token is for the client and is not sent.
  const tokenData = (await response.json()) as { access_token?: unknown };
  const token = tokenData.access_token;

  if (!token || typeof token !== 'string') {
    throw new Error('OIDC provider returned no access_token.');
  }

  return { token, baseUrl: pending.baseUrl };
}
