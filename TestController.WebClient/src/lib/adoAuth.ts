import {
  PublicClientApplication,
  InteractionRequiredAuthError,
  type AccountInfo,
  type Configuration,
} from '@azure/msal-browser';

/**
 * Delegated Azure DevOps access for the WebClient.
 *
 * The user signs in with their own Entra identity and we forward the resulting ADO token to our API, which
 * uses it for that request only (see AdoUserTokenMiddleware / AmbientAdoTokenProvider). This mirrors what the
 * WPF host does via InteractiveTokenProvider, and means the web tier no longer depends on a PAT that expires.
 *
 * Uses raw fetch, not apiFetch, so api.ts can import this without a circular dependency.
 */
export interface AdoAuthConfig {
  enabled: boolean;
  tenantId: string;
  clientId: string;
  scope: string;
  /** False when the page is served over plain HTTP — the API will refuse the token, so don't offer sign-in. */
  secureTransport: boolean;
}

let config: AdoAuthConfig | null = null;
let msal: PublicClientApplication | null = null;
let initialized = false;

const API_BASE = import.meta.env.VITE_API_BASE_URL || '';

export async function initAdoAuth(): Promise<AdoAuthConfig | null> {
  if (initialized) return config;
  initialized = true;

  try {
    const response = await fetch(`${API_BASE}/api/impact/ado-auth-config`);
    if (!response.ok) return null;
    config = (await response.json()) as AdoAuthConfig;
  } catch {
    return null;
  }

  if (!config?.enabled || !config.clientId) return config;

  const options: Configuration = {
    auth: {
      clientId: config.clientId,
      authority: `https://login.microsoftonline.com/${config.tenantId}`,
      redirectUri: window.location.origin,
    },
    // Session-scoped so the token dies with the tab rather than lingering in localStorage.
    cache: { cacheLocation: 'sessionStorage' },
  };

  msal = new PublicClientApplication(options);
  await msal.initialize();
  await msal.handleRedirectPromise().catch(() => undefined);
  return config;
}

export function adoAuthConfig(): AdoAuthConfig | null {
  return config;
}

export function adoAccount(): AccountInfo | null {
  return msal?.getAllAccounts()[0] ?? null;
}

export function adoSignedInAs(): string | null {
  return adoAccount()?.username ?? null;
}

export async function signInToAdo(): Promise<string | null> {
  if (!msal || !config) return null;
  const result = await msal.loginPopup({ scopes: [config.scope] });
  return result.account?.username ?? null;
}

export async function signOutOfAdo(): Promise<void> {
  const account = adoAccount();
  if (!msal || !account) return;
  await msal.clearCache({ account });
}

/** Cached-or-silently-refreshed ADO token. Null when not configured or not signed in. */
export async function getAdoToken(): Promise<string | null> {
  const account = adoAccount();
  if (!msal || !config || !account) return null;

  try {
    const result = await msal.acquireTokenSilent({ scopes: [config.scope], account });
    return result.accessToken;
  } catch (err) {
    // A silent failure here must not break the request — the API falls back to the host credential.
    if (err instanceof InteractionRequiredAuthError) return null;
    return null;
  }
}
