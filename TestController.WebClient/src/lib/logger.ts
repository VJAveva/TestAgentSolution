/**
 * Centralized client-side error logger.
 *
 * Goal: when any page fails to load, surface a clear, structured log entry
 * in the browser console (and optionally POST to the server) instead of
 * silently swallowing the error with `.catch(() => {})`.
 *
 * Usage:
 *   import { logError } from '../lib/logger';
 *   useEffect(() => { fetchBuilds().catch(err => logError('BuildList', 'fetchBuilds', err)); }, []);
 */

interface ApiError {
  status?: number;
  error?: string;
  detail?: string;
  correlationId?: string;
  message?: string;
  config?: { url?: string; method?: string; baseURL?: string };
  response?: { status?: number; data?: unknown };
}

/**
 * Logs an error to the browser console with a consistent, grep-able format.
 * Always returns the original error so callers can chain `.catch(logError(...))`
 * when they want to swallow without losing visibility.
 *
 * @param scope    Component or hook name (e.g. 'BuildList', 'useResults')
 * @param action   What was being attempted (e.g. 'fetchBuilds')
 * @param err      The thrown error (apiFetch error object, axios error, or Error)
 */
export function logError(scope: string, action: string, err: unknown): void {
  const e = err as ApiError;
  const cid = e?.correlationId ?? '?';
  const status = e?.status ?? e?.response?.status ?? 0;
  const url = e?.config?.url
    ? `${e.config.baseURL ?? ''}${e.config.url}`
    : '';
  const detail = e?.detail ?? e?.error ?? e?.message ?? String(err);

  // Single, consistent line ? easy to grep in the console:
  //   [ERR] [BuildList:fetchBuilds] [cid=ab12cd34] 500 GET /api/results/builds ? reason
  console.error(
    `[ERR] [${scope}:${action}] [cid=${cid}] ${status}${url ? ` ${url}` : ''} ? ${detail}`,
    err);

  // Optional: POST to a server-side ingestion endpoint for centralized logs.
  // Disabled by default to avoid request loops if the server itself is down.
  // Enable by setting VITE_REPORT_CLIENT_ERRORS=true and implementing
  // POST /api/clientlogs on the server.
  if (import.meta.env.VITE_REPORT_CLIENT_ERRORS === 'true') {
    try {
      const baseUrl = import.meta.env.VITE_API_BASE_URL || '';
      void fetch(`${baseUrl}/api/clientlogs`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          scope, action, status, url, detail, correlationId: cid,
          timestamp: new Date().toISOString(),
          userAgent: navigator.userAgent,
          page: window.location.pathname,
        }),
        keepalive: true,
      }).catch(() => { /* never throw from a logger */ });
    } catch { /* swallow ? logger must never break the app */ }
  }
}

/**
 * Curried helper for `.catch(...)`:
 *   .catch(logCatch('BuildList', 'fetchBuilds'))
 * Always swallows the error after logging, returning undefined.
 */
export function logCatch(scope: string, action: string): (err: unknown) => void {
  return (err) => logError(scope, action, err);
}
