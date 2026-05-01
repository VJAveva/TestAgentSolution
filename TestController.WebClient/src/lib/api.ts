import { getUserId } from './userIdentity';
import { appLogger } from './logger';

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
function generateId(): string {
  if (typeof crypto !== 'undefined' && crypto.randomUUID) {
    return crypto.randomUUID().slice(0, 8);
  }
  return Math.random().toString(36).slice(2, 10);
}

export async function apiFetch<T>(
  path: string,
  options?: RequestInit
): Promise<T> {
  const correlationId = generateId();
  const url = `${API_BASE}${path}`;
  const method = options?.method || 'GET';

  console.log(`[API] [${correlationId}] ${method} ${url}`);
  appLogger.debug('API', `${method} ${path}`, { correlationId });

  const startTime = performance.now();

  try {
    const response = await fetch(url, {
      ...options,
      headers: {
        'Content-Type': 'application/json',
        'X-Request-Id': correlationId,
        'X-User-Id': getUserId(),
        'X-Source': 'WebClient',
        ...options?.headers,
      },
    });

    const elapsed = (performance.now() - startTime).toFixed(0);

    // Detect SPA fallback returning HTML instead of JSON
    const contentType = response.headers.get('content-type') || '';
    if (contentType.includes('text/html') && path.startsWith('/api')) {
      const errorMsg = `ROUTE NOT FOUND — got HTML instead of JSON. Endpoint ${path} may not be registered.`;
      console.error(`[API] [${correlationId}] ${errorMsg}`);
      appLogger.error('API', errorMsg, { path, correlationId }, correlationId);
      throw {
        status: 404,
        error: 'Route not found',
        detail: `${path} returned HTML — endpoint not registered`,
        correlationId,
      };
    }

    if (!response.ok) {
      const body = await response.json().catch(() => ({}));
      console.error(
        `[API] [${correlationId}] ${response.status} ${url} (${elapsed}ms)`,
        body
      );
      appLogger.error('API', `${response.status} ${method} ${path} (${elapsed}ms)`, {
        status: response.status, body, correlationId,
      }, correlationId);
      throw {
        status: response.status,
        error: body.error || response.statusText,
        detail: body.detail,
        correlationId: body.correlationId || correlationId,
      };
    }

    const data = await response.json();
    console.log(`[API] [${correlationId}] 200 ${url} (${elapsed}ms)`);
    appLogger.info('API', `200 ${method} ${path} (${elapsed}ms)`, { correlationId });
    return data as T;

  } catch (err: any) {
    // Re-throw structured errors from above
    if (err.status !== undefined) throw err;

    const elapsed = (performance.now() - startTime).toFixed(0);
    console.error(
      `[API] [${correlationId}] NETWORK ERROR ${url} (${elapsed}ms)`,
      err.message
    );
    appLogger.error('API', `NETWORK ERROR ${method} ${path} (${elapsed}ms): ${err.message}`, {
      correlationId, detail: err.message,
    }, correlationId);
    throw {
      status: 0,
      error: 'Network Error',
      detail: `Cannot reach ${url}. ${err.message}`,
      correlationId,
    };
  }
}
