import { useSyncExternalStore } from 'react';
import { appLogger, type AppLogEntry, type LogLevel } from '../lib/logger';

/** React hook to subscribe to live app log entries. */
export function useAppLog() {
  const entries = useSyncExternalStore(
    appLogger.subscribe,
    appLogger.getEntries,
  );
  return entries;
}

/** React hook to get error count (for badge display). */
export function useAppLogCounts(): Record<LogLevel, number> {
  useSyncExternalStore(appLogger.subscribe, appLogger.getEntries);
  return appLogger.counts();
}

export type { AppLogEntry, LogLevel };
