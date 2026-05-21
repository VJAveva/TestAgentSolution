import { useState, useEffect, useCallback, useRef } from 'react';
import axios from 'axios';
import type { FleetResponse, FleetAgent } from '../types/agentWorkspace';
import { useSignalR } from './useSignalR';
import { errorThrottle } from '../lib/errorThrottle';

/**
 * Hook to load fleet state (all agents + lock status) and auto-refresh
 * on SignalR events (AgentStatusChanged, AgentLocksChanged).
 */
export function useFleetState() {
  const [fleet, setFleet] = useState<FleetAgent[]>([]);
  const [lockVersion, setLockVersion] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const connection = useSignalR();
  const mountedRef = useRef(true);

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

  // Initial load
  useEffect(() => {
    mountedRef.current = true;
    fetchFleet();
    return () => { mountedRef.current = false; };
  }, [fetchFleet]);

  // SignalR event subscriptions for live updates
  useEffect(() => {
    if (!connection) return;

    const onStatusChanged = () => { fetchFleet(); };
    const onLocksChanged = () => { fetchFleet(); };

    connection.on('AgentStatusChanged', onStatusChanged);
    connection.on('AgentLocksChanged', onLocksChanged);

    return () => {
      connection.off('AgentStatusChanged', onStatusChanged);
      connection.off('AgentLocksChanged', onLocksChanged);
    };
  }, [connection, fetchFleet]);

  return { fleet, lockVersion, loading, error, refresh: fetchFleet };
}
