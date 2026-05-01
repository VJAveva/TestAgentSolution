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

const MAX_ENTRIES = 500;
const STORAGE_KEY = 'app-log';

type Listener = () => void;
const listeners = new Set<Listener>();

let entries: AppLogEntry[] = [];

// Hydrate from sessionStorage on load
try {
  const stored = sessionStorage.getItem(STORAGE_KEY);
  if (stored) entries = JSON.parse(stored);
} catch { /* ignore parse errors */ }

function persist() {
  try {
    sessionStorage.setItem(STORAGE_KEY, JSON.stringify(entries.slice(-MAX_ENTRIES)));
  } catch { /* quota exceeded — drop oldest */ }
}

function notify() {
  listeners.forEach(fn => fn());
}

function generateId(): string {
  if (typeof crypto !== 'undefined' && crypto.randomUUID) {
    return crypto.randomUUID().slice(0, 8);
  }
  return Math.random().toString(36).slice(2, 10);
}

function write(level: LogLevel, category: string, message: string, data?: unknown, correlationId?: string) {
  const entry: AppLogEntry = {
    id: generateId(),
    timestamp: new Date().toISOString(),
    level,
    category,
    message,
    data: data !== undefined ? data : undefined,
    correlationId,
  };
  entries.push(entry);
  if (entries.length > MAX_ENTRIES) entries = entries.slice(-MAX_ENTRIES);
  persist();
  notify();
}

export const appLogger = {
  debug: (category: string, message: string, data?: unknown) => write('debug', category, message, data),
  info:  (category: string, message: string, data?: unknown) => write('info', category, message, data),
  warn:  (category: string, message: string, data?: unknown) => write('warn', category, message, data),
  error: (category: string, message: string, data?: unknown, correlationId?: string) =>
    write('error', category, message, data, correlationId),

  getEntries: () => entries,
  clear: () => { entries = []; persist(); notify(); },

  subscribe: (fn: Listener) => { listeners.add(fn); return () => { listeners.delete(fn); }; },

  /** Count entries by level */
  counts: () => {
    const c = { debug: 0, info: 0, warn: 0, error: 0 };
    for (const e of entries) c[e.level]++;
    return c;
  },
};
