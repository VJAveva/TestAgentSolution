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

export type LogLevel = 'debug' | 'info' | 'warn' | 'error';

export interface AppLogEntry {
  id: string;
  timestamp: string;
  level: LogLevel;
  category: string;
  message: string;
  data?: unknown;
  correlationId?: string;
}

type Listener = () => void;

let nextId = 1;
let entries: AppLogEntry[] = [];
let listeners: Set<Listener> = new Set();

function emit() {
  listeners.forEach(l => l());
}

/**
 * Ships a single error-level entry to the shared server log
 * (POST /api/clientlogs → IAppLogger). Gated by VITE_REPORT_CLIENT_ERRORS so
 * it stays opt-in. Never throws — a logger must not become a failure surface.
 */
function reportToServer(entry: AppLogEntry) {
  if (import.meta.env.VITE_REPORT_CLIENT_ERRORS !== 'true') return;
  try {
    const baseUrl = import.meta.env.VITE_API_BASE_URL || '';
    void fetch(`${baseUrl}/api/clientlogs`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        level: entry.level,
        scope: entry.category,
        detail: entry.message,
        correlationId: entry.correlationId,
        timestamp: entry.timestamp,
        userAgent: navigator.userAgent,
        page: window.location.pathname,
      }),
      keepalive: true,
    }).catch(() => { /* never throw from a logger */ });
  } catch { /* swallow — logger must never break the app */ }
}

export const appLogger = {
  log(level: LogLevel, category: string, message: string, data?: unknown, correlationId?: string) {
    const entry: AppLogEntry = { id: String(nextId++), timestamp: new Date().toISOString(), level, category, message, data, correlationId };
    entries = [...entries, entry];
    emit();
    // Ship errors to the shared server log so client crashes land next to the
    // server-side failure in errors_{date}.log / app_{date}.jsonl.
    if (level === 'error') reportToServer(entry);
  },
  debug(category: string, message: string, data?: unknown) { appLogger.log('debug', category, message, data); },
  info(category: string, message: string, data?: unknown) { appLogger.log('info', category, message, data); },
  warn(category: string, message: string, data?: unknown) { appLogger.log('warn', category, message, data); },
  error(category: string, message: string, data?: unknown, correlationId?: string) { appLogger.log('error', category, message, data, correlationId); },
  subscribe(listener: Listener) {
    listeners.add(listener);
    return () => { listeners.delete(listener); };
  },
  getEntries() { return entries; },
  counts(): Record<LogLevel, number> {
    const c: Record<LogLevel, number> = { debug: 0, info: 0, warn: 0, error: 0 };
    for (const e of entries) c[e.level]++;
    return c;
  },
  clear() {
    entries = [];
    emit();
  },
};

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

  const message = `[${scope}:${action}] ${status}${url ? ` ${url}` : ''} \u2014 ${detail}`;

  // Write to appLogger so AppLogPanel can display it. appLogger.error ships
  // error-level entries to POST /api/clientlogs via reportToServer, so the
  // message (which already embeds scope/action/status/url) reaches the server.
  appLogger.error(scope, message, err, cid);

  // Also write to console for dev tools
  console.error(`[ERR] [cid=${cid}] ${message}`, err);
}

/**
 * Curried helper for `.catch(...)`:
 *   .catch(logCatch('BuildList', 'fetchBuilds'))
 * Always swallows the error after logging, returning undefined.
 */
export function logCatch(scope: string, action: string): (err: unknown) => void {
  return (err) => logError(scope, action, err);
}
