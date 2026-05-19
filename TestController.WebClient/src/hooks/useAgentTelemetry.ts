import { useState, useEffect, useRef, useCallback } from 'react';
import axios from 'axios';
import type { AgentTelemetry } from '../types/agentWorkspace';

/**
 * Hook to poll agent telemetry at a configurable interval (default 2s).
 * Returned data maps to the /api/agents/{name}/telemetry endpoint.
 */
export function useAgentTelemetry(agentName: string | null, intervalMs = 2000) {
  const [telemetry, setTelemetry] = useState<AgentTelemetry | null>(null);
  const [error, setError] = useState<string | null>(null);
  const mountedRef = useRef(true);
  const timerRef = useRef<ReturnType<typeof setInterval> | null>(null);

  const fetchTelemetry = useCallback(async () => {
    if (!agentName) return;
    try {
      const { data } = await axios.get<AgentTelemetry>(
        `/api/agents/${encodeURIComponent(agentName)}/telemetry`
      );
      if (mountedRef.current) {
        setTelemetry(data);
        setError(null);
      }
    } catch (err: any) {
      if (mountedRef.current) {
        setError(err?.message ?? 'Telemetry fetch failed');
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

    // Set up polling
    timerRef.current = setInterval(fetchTelemetry, intervalMs);

    return () => {
      mountedRef.current = false;
      if (timerRef.current) clearInterval(timerRef.current);
    };
  }, [agentName, intervalMs, fetchTelemetry]);

  return { telemetry, error, refresh: fetchTelemetry };
}
