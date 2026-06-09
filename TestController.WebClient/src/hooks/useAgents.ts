import { useCallback } from 'react';
import axios from 'axios';
import { useAgentStore } from '../stores/agentStore';
import type { AgentInfo, DiagnosticStep } from '../types/api';

export function useAgents() {
  const setAgents = useAgentStore(s => s.setAgents);

  const fetchAgents = useCallback(async () => {
    const { data } = await axios.get<AgentInfo[]>('/api/agents');
    setAgents(data);
    return data;
  }, [setAgents]);

  const registerAgent = useCallback(async (name: string, address: string) => {
    const { data } = await axios.post('/api/agents/register', { name, address });
    await fetchAgents();
    return data as { name: string; address: string; status: string; healthy: boolean; message: string; detail: string | null };
  }, [fetchAgents]);

  const unregisterAgent = useCallback(async (name: string) => {
    await axios.delete(`/api/agents/${encodeURIComponent(name)}`);
    await fetchAgents();
  }, [fetchAgents]);

  const testAgent = useCallback(async (name: string) => {
    const { data } = await axios.post(`/api/agents/${encodeURIComponent(name)}/test`);
    await fetchAgents();
    return data as { name: string; status: string; message: string };
  }, [fetchAgents]);

  const diagnoseAgent = useCallback(async (name: string) => {
    const { data } = await axios.post(`/api/agents/${encodeURIComponent(name)}/diagnose`);
    return data as { name: string; steps: DiagnosticStep[]; summary: string };
  }, []);

  const getSnapshot = useCallback(async (name: string) => {
    const { data } = await axios.get(`/api/agents/${encodeURIComponent(name)}/snapshot`);
    return data;
  }, []);

  const getHealth = useCallback(async (name: string) => {
    const { data } = await axios.get(`/api/agents/${encodeURIComponent(name)}/health`);
    return data;
  }, []);

  const getHistory = useCallback(async (name: string, max = 20) => {
    const { data } = await axios.get(`/api/agents/${encodeURIComponent(name)}/history`, { params: { max } });
    return data;
  }, []);

  const getAudit = useCallback(async (name: string, max = 500) => {
    const { data } = await axios.get(`/api/agents/${encodeURIComponent(name)}/audit`, { params: { max } });
    return data;
  }, []);

  return { fetchAgents, registerAgent, unregisterAgent, testAgent, diagnoseAgent, getSnapshot, getHealth, getHistory, getAudit };
}
