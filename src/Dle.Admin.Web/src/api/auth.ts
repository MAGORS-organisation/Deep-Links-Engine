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

/** Initiates an interactive OAuth2/OIDC PKCE authorization flow. */
export async function initiateOidcLogin(
  baseUrl: string,
  authority: string,
  clientId: string,
  scopes: string[] = ['openid', 'profile', 'email'],
): Promise<void> {
  const normalizedAuthority = authority.replace(/\/+$/, '');
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
  try {
    const disco = await fetch(`${normalizedAuthority}/.well-known/openid-configuration`);
    if (disco.ok) {
      const discoJson = await disco.json();
      if (typeof discoJson.authorization_endpoint === 'string') {
        authEndpoint = discoJson.authorization_endpoint;
      }
    }
  } catch {
    // Keep fallback
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

  let pending: PendingOidc;
  try {
    pending = JSON.parse(stored) as PendingOidc;
  } catch {
    return null;
  }

  if (state !== pending.state) {
    throw new Error('OIDC state parameter mismatch.');
  }

  // Token endpoint discovery
  let tokenEndpoint = `${pending.authority}/protocol/openid-connect/token`;
  try {
    const disco = await fetch(`${pending.authority}/.well-known/openid-configuration`);
    if (disco.ok) {
      const discoJson = await disco.json();
      if (typeof discoJson.token_endpoint === 'string') {
        tokenEndpoint = discoJson.token_endpoint;
      }
    }
  } catch {
    // Keep fallback
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

  const tokenData = await response.json();
  const token = typeof tokenData.access_token === 'string' ? tokenData.access_token : tokenData.id_token;

  if (!token || typeof token !== 'string') {
    throw new Error('OIDC provider returned no access_token.');
  }

  // Clean URL query parameters
  const cleanUrl = new URL(window.location.href);
  cleanUrl.searchParams.delete('code');
  cleanUrl.searchParams.delete('state');
  cleanUrl.searchParams.delete('session_state');
  window.history.replaceState({}, '', cleanUrl.toString());

  return { token, baseUrl: pending.baseUrl };
}
