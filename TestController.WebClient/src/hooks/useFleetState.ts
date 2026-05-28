import { useState, useEffect, useCallback, useRef } from 'react';
import axios from 'axios';
import type { FleetResponse, FleetAgent } from '../types/agentWorkspace';
import { useSignalR } from './useSignalR';
import { errorThrottle } from '../lib/errorThrottle';

/**
 * Hook to load fleet state (all agents + lock status) and auto-refresh
 * on SignalR events (AgentStatusChanged, AgentLocksChanged).
 * Uses 500ms debounce to coalesce rapid events from 200 agents.
 */
export function useFleetState() {
  const [fleet, setFleet] = useState<FleetAgent[]>([]);
  const [lockVersion, setLockVersion] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const connection = useSignalR();
  const mountedRef = useRef(true);
  const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const fetchFleet = useCallback(async () => {
    try {
      const { data } = await axios.get<FleetResponse>('/api/agents/fleet');
      if (mountedRef.current) {
        setFleet(data.agents);
        setLockVersion(data.lockVersion);
        setError(null);
        errorThrottle.reset('fleet');
      }
    } catch (err: any) {
      if (mountedRef.current) {
        if (errorThrottle.shouldReport('fleet')) {
          setError(err?.message ?? 'Failed to fetch fleet');
        }
      }
    } finally {
      if (mountedRef.current) setLoading(false);
    }
  }, []);

  // Debounced fetch: coalesces rapid SignalR events into a single API call
  const debouncedFetch = useCallback(() => {
    if (debounceRef.current) clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(() => {
      fetchFleet();
    }, 500);
  }, [fetchFleet]);

  // Initial load
  useEffect(() => {
    mountedRef.current = true;
    fetchFleet();
    return () => {
      mountedRef.current = false;
      if (debounceRef.current) clearTimeout(debounceRef.current);
    };
  }, [fetchFleet]);

  // SignalR event subscriptions for live updates (debounced)
  useEffect(() => {
    if (!connection) return;

    const onStatusChanged = () => { debouncedFetch(); };
    const onLocksChanged = () => { debouncedFetch(); };

    connection.on('AgentStatusChanged', onStatusChanged);
    connection.on('AgentLocksChanged', onLocksChanged);

    return () => {
      connection.off('AgentStatusChanged', onStatusChanged);
      connection.off('AgentLocksChanged', onLocksChanged);
    };
  }, [connection, debouncedFetch]);

  return { fleet, lockVersion, loading, error, refresh: fetchFleet };
}
