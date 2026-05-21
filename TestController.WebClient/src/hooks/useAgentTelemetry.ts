import { useState, useEffect, useRef, useCallback } from 'react';
import axios from 'axios';
import type { AgentTelemetry } from '../types/agentWorkspace';
import { useConnectionStore } from '../stores/connectionStore';
import { errorThrottle } from '../lib/errorThrottle';

/** Default polling interval when no execution is active. */
const DEFAULT_INTERVAL_MS = 2000;
/** Backed-off interval when SignalR indicates active execution. */
const BACKOFF_INTERVAL_MS = 15000;

/**
 * Hook to poll agent telemetry at a configurable interval (default 2s).
 * Automatically backs off to 15s when the SignalR connection is active
 * and streaming execution events (active sessions detected).
 *
 * This prevents hammering the telemetry endpoint during installs/executions
 * when real-time state is already pushed via SignalR.
 */
export function useAgentTelemetry(agentName: string | null, intervalMs = DEFAULT_INTERVAL_MS) {
  const [telemetry, setTelemetry] = useState<AgentTelemetry | null>(null);
  const [error, setError] = useState<string | null>(null);
  const mountedRef = useRef(true);
  const timerRef = useRef<ReturnType<typeof setInterval> | null>(null);
  const consecutiveFailures = useRef(0);

  const signalRStatus = useConnectionStore(s => s.status);

  // Back off when connected and likely receiving live events
  const effectiveInterval = signalRStatus === 'connected'
    ? BACKOFF_INTERVAL_MS
    : intervalMs;

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

    // Set up polling with the effective interval
    timerRef.current = setInterval(fetchTelemetry, effectiveInterval);

    return () => {
      mountedRef.current = false;
      if (timerRef.current) clearInterval(timerRef.current);
    };
  }, [agentName, effectiveInterval, fetchTelemetry]);

  return { telemetry, error, refresh: fetchTelemetry };
}
