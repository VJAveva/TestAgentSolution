import { useState, useEffect, useRef, useCallback } from 'react';
import axios from 'axios';
import type { AgentTelemetry } from '../types/agentWorkspace';
import { errorThrottle } from '../lib/errorThrottle';

/** Default polling interval for the agent monitor page. */
const DEFAULT_INTERVAL_MS = 2000;

/**
 * Hook to poll agent telemetry at a configurable interval (default 2s).
 *
 * Always polls at the default interval regardless of SignalR state because:
 * - SignalR does NOT push individual agent telemetry to the monitor page
 * - The WebAPI caches telemetry and returns it without hitting the agent gRPC
 *   during execution, so the poll is cheap and safe
 * - The monitor page MUST stay responsive (showing current command, CPU, etc.)
 *   even during active sessions — that's the whole point of the monitor
 */
export function useAgentTelemetry(agentName: string | null, intervalMs = DEFAULT_INTERVAL_MS) {
  const [telemetry, setTelemetry] = useState<AgentTelemetry | null>(null);
  const [error, setError] = useState<string | null>(null);
  const mountedRef = useRef(true);
  const timerRef = useRef<ReturnType<typeof setInterval> | null>(null);
  const consecutiveFailures = useRef(0);

  const fetchTelemetry = useCallback(async () => {
    if (!agentName) return;
    try {
      const { data } = await axios.get<AgentTelemetry>(
        `/api/agents/${encodeURIComponent(agentName)}/telemetry`
      );
      if (mountedRef.current) {
        setTelemetry(data);
        setError(null);
        consecutiveFailures.current = 0;
        errorThrottle.reset('telemetry', agentName ?? '');
      }
    } catch (err: any) {
      if (mountedRef.current) {
        consecutiveFailures.current++;
        if (errorThrottle.shouldReport('telemetry', agentName ?? '')) {
          setError(err?.message ?? 'Telemetry fetch failed');
        }
      }
    }
  }, [agentName]);

  useEffect(() => {
    mountedRef.current = true;

    if (!agentName) {
      setTelemetry(null);
      setError(null);
      return;
    }

    // Fetch immediately on mount/agent change
    fetchTelemetry();

    // Set up polling at the configured interval
    timerRef.current = setInterval(fetchTelemetry, intervalMs);

    return () => {
      mountedRef.current = false;
      if (timerRef.current) clearInterval(timerRef.current);
    };
  }, [agentName, intervalMs, fetchTelemetry]);

  return { telemetry, error, refresh: fetchTelemetry };
}
