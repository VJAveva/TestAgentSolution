import { getUserId } from './userIdentity';

const API_BASE = import.meta.env.VITE_API_BASE_URL || '';

/**
 * Fetch wrapper with correlation ID tracking and structured error handling.
 *
 * Features:
 *   - Assigns X-Request-Id to every request (logged server-side)
 *   - Sends X-User-Id for session ownership and lock tracking
 *   - Detects HTML responses on /api/ routes (SPA rewrite misconfiguration)
 *   - Logs request/response timing to browser console
 *   - Returns structured error objects with correlationId for debugging
 */
export async function apiFetch<T>(
  path: string,
  options?: RequestInit
): Promise<T> {
  const correlationId = (crypto.randomUUID?.() ?? Math.random().toString(36).slice(2, 10)).slice(0, 8);
  const url = `${API_BASE}${path}`;
  const method = options?.method || 'GET';

  console.log(`[API] [${correlationId}] ${method} ${url}`);

  const startTime = performance.now();

  try {
    // Build headers: auto-attach auth token from sessionStorage when available.
    // Callers can still override by passing their own Authorization header.
    const autoHeaders: Record<string, string> = {
      'Content-Type': 'application/json',
      'X-Request-Id': correlationId,
      'X-User-Id': getUserId(),
      'X-Source': 'WebClient',
    };
    const token = sessionStorage.getItem('auth_token');
    if (token) {
      autoHeaders['Authorization'] = `Bearer ${token}`;
    }

    const response = await fetch(url, {
      ...options,
      headers: {
        ...autoHeaders,
        ...options?.headers,
      },
    });

    const elapsed = (performance.now() - startTime).toFixed(0);

    // Detect SPA fallback returning HTML instead of JSON
    const contentType = response.headers.get('content-type') || '';
    if (contentType.includes('text/html') && path.startsWith('/api')) {
      console.error(
        `[API] [${correlationId}] ROUTE NOT FOUND � got HTML instead of JSON. ` +
        `Endpoint ${path} may not be registered. Check IIS SPA rewrite rules.`
      );
      throw {
        status: 404,
        error: 'Route not found',
        detail: `${path} returned HTML � endpoint not registered`,
        correlationId,
      };
    }

    if (!response.ok) {
      const body = await response.json().catch(() => ({}));
      console.error(
        `[API] [${correlationId}] ${response.status} ${url} (${elapsed}ms)`,
        body
      );

      // 401 handling — token invalid/expired: clear auth so the UI returns to login.
      if (response.status === 401 && !path.includes('/api/auth/')) {
        sessionStorage.removeItem('auth_token');
        const { useAuthStore } = await import('../stores/authStore');
        const st = useAuthStore.getState();
        if (st.isAuthenticated) {
          useAuthStore.setState({ user: null, token: null, isAuthenticated: false, mustChangePassword: false, error: null });
        }
      }

      // Phase 2c: 403 handling — show toast + refresh stale capabilities
      if (response.status === 403) {
        const { useAuthDeniedToast } = await import('../components/common/AuthDeniedToast');
        const { useAuthStore } = await import('../stores/authStore');
        useAuthDeniedToast.getState().show(body.error || 'Permission denied');
        // Fire-and-forget: refresh capabilities so UI rebinds
        useAuthStore.getState().fetchMe().catch(() => {});
      }

      throw {
        status: response.status,
        error: body.error || response.statusText,
        detail: body.detail,
        reasonCode: body.reasonCode,
        correlationId: body.correlationId || correlationId,
        body,
      };
    }

    const data = await response.json();
    console.log(`[API] [${correlationId}] 200 ${url} (${elapsed}ms)`);
    return data as T;

  } catch (err: any) {
    // Re-throw structured errors from above
    if (err.status !== undefined) throw err;

    const elapsed = (performance.now() - startTime).toFixed(0);
    console.error(
      `[API] [${correlationId}] NETWORK ERROR ${url} (${elapsed}ms)`,
      err.message
    );
    throw {
      status: 0,
      error: 'Network Error',
      detail: `Cannot reach ${url}. ${err.message}`,
      correlationId,
    };
  }
}

/** Typed verb helpers over apiFetch to cut call-site boilerplate. */
export const apiGet = <T>(path: string) => apiFetch<T>(path);

export const apiPost = <T>(path: string, body?: unknown) =>
  apiFetch<T>(path, {
    method: 'POST',
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });

export const apiPut = <T>(path: string, body?: unknown) =>
  apiFetch<T>(path, {
    method: 'PUT',
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });

export const apiDelete = <T>(path: string) => apiFetch<T>(path, { method: 'DELETE' });

/**
 * Fetch a non-JSON payload (blob/text downloads) through the same
 * auth/correlation pipeline as apiFetch, without JSON parsing.
 */
export async function apiFetchRaw(path: string, options?: RequestInit): Promise<Response> {
  const correlationId = (crypto.randomUUID?.() ?? Math.random().toString(36).slice(2, 10)).slice(0, 8);
  const url = `${API_BASE}${path}`;
  const autoHeaders: Record<string, string> = {
    'X-Request-Id': correlationId,
    'X-User-Id': getUserId(),
    'X-Source': 'WebClient',
  };
  const token = sessionStorage.getItem('auth_token');
  if (token) autoHeaders['Authorization'] = `Bearer ${token}`;

  const response = await fetch(url, {
    ...options,
    headers: { ...autoHeaders, ...options?.headers },
  });
  if (!response.ok) {
    throw { status: response.status, error: response.statusText, correlationId };
  }
  return response;
}
