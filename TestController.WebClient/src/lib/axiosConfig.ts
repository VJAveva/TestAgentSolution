import axios from 'axios';
import { getUserId } from './userIdentity';

/**
 * Configures the global axios instance.
 *
 * In production, the WebClient may be deployed to a different origin
 * than the WebApi. Without a baseURL, calls like `axios.get('/api/...')`
 * resolve against the WebClient's own origin and silently fail (404 HTML
 * from the SPA fallback), which is the root cause of "API not loading"
 * symptoms in production.
 *
 * We mirror the same behavior `apiFetch` uses:
 *   - baseURL from VITE_API_BASE_URL (empty in dev ? relative URLs hit the Vite proxy)
 *   - X-User-Id and X-Source headers on every request
 *   - X-Request-Id correlation header for server-side log tracing
 */
export function configureAxios(): void {
  const baseUrl = import.meta.env.VITE_API_BASE_URL || '';
  if (baseUrl) {
    axios.defaults.baseURL = baseUrl;
  }

  axios.defaults.headers.common['X-User-Id'] = getUserId();
  axios.defaults.headers.common['X-Source'] = 'WebClient';

  // Per-request correlation id (mirrors apiFetch behavior).
  axios.interceptors.request.use(config => {
    config.headers = config.headers ?? {};
    if (!config.headers['X-Request-Id']) {
      config.headers['X-Request-Id'] = crypto.randomUUID().slice(0, 8);
    }
    return config;
  });

  // Log failures so production issues surface in the browser console with
  // the same shape as apiFetch errors.
  axios.interceptors.response.use(
    response => response,
    error => {
      const cfg = error?.config;
      const cid = cfg?.headers?.['X-Request-Id'] ?? '?';
      const url = (cfg?.baseURL ?? '') + (cfg?.url ?? '');
      const status = error?.response?.status ?? 0;
      console.error(
        `[axios] [${cid}] ${status} ${cfg?.method?.toUpperCase() ?? 'GET'} ${url}`,
        error?.response?.data ?? error?.message);
      return Promise.reject(error);
    });
}
