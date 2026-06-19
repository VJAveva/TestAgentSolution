import { useCallback } from 'react';
import { apiGet, apiPost, apiDelete } from '../lib/api';
import { useAgentStore } from '../stores/agentStore';
import type { AgentInfo, DiagnosticStep } from '../types/api';

export function useAgents() {
  const setAgents = useAgentStore(s => s.setAgents);

  const fetchAgents = useCallback(async () => {
    const data = await apiGet<AgentInfo[]>('/api/agents');
    setAgents(data);
    return data;
  }, [setAgents]);

  const registerAgent = useCallback(async (name: string, address: string) => {
    const data = await apiPost('/api/agents/register', { name, address });
    await fetchAgents();
    return data as { name: string; address: string; status: string; healthy: boolean; message: string; detail: string | null };
  }, [fetchAgents]);

  const unregisterAgent = useCallback(async (name: string) => {
    await apiDelete(`/api/agents/${encodeURIComponent(name)}`);
    await fetchAgents();
  }, [fetchAgents]);

  const testAgent = useCallback(async (name: string) => {
    const data = await apiPost(`/api/agents/${encodeURIComponent(name)}/test`);
    await fetchAgents();
    return data as { name: string; status: string; message: string };
  }, [fetchAgents]);

  const diagnoseAgent = useCallback(async (name: string) => {
    const data = await apiPost(`/api/agents/${encodeURIComponent(name)}/diagnose`);
    return data as { name: string; steps: DiagnosticStep[]; summary: string };
  }, []);

  const getSnapshot = useCallback(async (name: string) => {
    return apiGet<Record<string, unknown>>(`/api/agents/${encodeURIComponent(name)}/snapshot`);
  }, []);

  const getHealth = useCallback(async (name: string) => {
    return apiGet<Record<string, unknown>>(`/api/agents/${encodeURIComponent(name)}/health`);
  }, []);

  const getHistory = useCallback(async (name: string, max = 20) => {
    return apiGet<Record<string, unknown>[]>(`/api/agents/${encodeURIComponent(name)}/history?max=${max}`);
  }, []);

  const getAudit = useCallback(async (name: string, max = 500) => {
    return apiGet<Record<string, unknown>[]>(`/api/agents/${encodeURIComponent(name)}/audit?max=${max}`);
  }, []);

  return { fetchAgents, registerAgent, unregisterAgent, testAgent, diagnoseAgent, getSnapshot, getHealth, getHistory, getAudit };
}
