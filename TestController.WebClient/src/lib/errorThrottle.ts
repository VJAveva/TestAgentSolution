/**
 * Error deduplication and backoff utility.
 *
 * Prevents flooding users with repeated identical error messages
 * during outages (CLIENT-005 hardening). Tracks recent errors by
 * endpoint/key and suppresses duplicates within a configurable window.
 *
 * Usage:
 *   import { errorThrottle } from '../lib/errorThrottle';
 *
 *   catch (err) {
 *     if (errorThrottle.shouldReport('telemetry', agentName)) {
 *       showToast(err.message);
 *     }
 *   }
 */

interface ThrottleEntry {
  /** Number of suppressed occurrences since last report */
  suppressed: number;
  /** Timestamp of last reported occurrence */
  lastReportedAt: number;
  /** Backoff interval in ms (doubles each time, capped) */
  currentBackoffMs: number;
}

const DEFAULT_INITIAL_BACKOFF_MS = 5_000;   // 5s before re-reporting same error
const DEFAULT_MAX_BACKOFF_MS = 60_000;      // cap at 1 minute
const CLEANUP_INTERVAL_MS = 120_000;        // clean stale entries every 2 min

class ErrorThrottle {
  private entries = new Map<string, ThrottleEntry>();
  private cleanupTimer: ReturnType<typeof setInterval> | null = null;
  private initialBackoffMs: number;
  private maxBackoffMs: number;

  constructor(
    initialBackoffMs = DEFAULT_INITIAL_BACKOFF_MS,
    maxBackoffMs = DEFAULT_MAX_BACKOFF_MS
  ) {
    this.initialBackoffMs = initialBackoffMs;
    this.maxBackoffMs = maxBackoffMs;
  }

  /**
   * Returns true if this error should be reported to the user.
   * Returns false if the same key was recently reported (within backoff window).
   *
   * @param category - Error category (e.g., 'telemetry', 'fleet', 'locks')
   * @param key - Specific identifier (e.g., agent name, endpoint path)
   */
  shouldReport(category: string, key: string = ''): boolean {
    const id = `${category}:${key}`;
    const now = Date.now();
    const entry = this.entries.get(id);

    if (!entry) {
      // First occurrence — report it
      this.entries.set(id, {
        suppressed: 0,
        lastReportedAt: now,
        currentBackoffMs: this.initialBackoffMs,
      });
      this.ensureCleanup();
      return true;
    }

    const elapsed = now - entry.lastReportedAt;
    if (elapsed >= entry.currentBackoffMs) {
      // Backoff expired — report again with increased backoff
      entry.lastReportedAt = now;
      entry.currentBackoffMs = Math.min(
        entry.currentBackoffMs * 2,
        this.maxBackoffMs
      );
      const suppressed = entry.suppressed;
      entry.suppressed = 0;
      if (suppressed > 0) {
        console.debug(
          `[ErrorThrottle] Reporting '${id}' after suppressing ${suppressed} duplicate(s)`
        );
      }
      return true;
    }

    // Within backoff window — suppress
    entry.suppressed++;
    return false;
  }

  /**
   * Resets the throttle state for a key (call on success to allow
   * immediate re-report if the error recurs).
   */
  reset(category: string, key: string = ''): void {
    this.entries.delete(`${category}:${key}`);
  }

  /** Resets all throttle state. */
  resetAll(): void {
    this.entries.clear();
  }

  /** Returns the number of suppressed occurrences for a key. */
  getSuppressedCount(category: string, key: string = ''): number {
    return this.entries.get(`${category}:${key}`)?.suppressed ?? 0;
  }

  private ensureCleanup(): void {
    if (this.cleanupTimer) return;
    this.cleanupTimer = setInterval(() => {
      const now = Date.now();
      for (const [id, entry] of this.entries) {
        // Remove entries that haven't been hit in 2x their backoff window
        if (now - entry.lastReportedAt > entry.currentBackoffMs * 2) {
          this.entries.delete(id);
        }
      }
      if (this.entries.size === 0 && this.cleanupTimer) {
        clearInterval(this.cleanupTimer);
        this.cleanupTimer = null;
      }
    }, CLEANUP_INTERVAL_MS);
  }
}

/** Global singleton for error throttling across the app. */
export const errorThrottle = new ErrorThrottle();
