import { useCallback } from 'react';
import { apiGet, apiPost, apiDelete } from '../lib/api';
import { useAgentStore } from '../stores/agentStore';
import type { AgentInfo, DiagnosticStep } from '../types/api';

/** Outcome of a bulk register/unregister operation. */
export interface BulkResult {
  succeeded: string[];
  failed: { name: string; error: string }[];
}

/** Maps Promise.allSettled results back to agent names for a bulk summary. */
function summarizeBulk(
  names: string[],
  results: PromiseSettledResult<unknown>[]
): BulkResult {
  const succeeded: string[] = [];
  const failed: { name: string; error: string }[] = [];
  results.forEach((r, i) => {
    if (r.status === 'fulfilled') {
      succeeded.push(names[i]);
    } else {
      const reason = r.reason as { detail?: string; error?: string } | undefined;
      failed.push({ name: names[i], error: reason?.detail || reason?.error || 'Failed' });
    }
  });
  return { succeeded, failed };
}

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

  // Bulk register: fire every registration simultaneously, refresh once at the end.
  const registerMany = useCallback(async (list: { name: string; address: string }[]): Promise<BulkResult> => {
    const results = await Promise.allSettled(
      list.map(a => apiPost('/api/agents/register', { name: a.name, address: a.address }))
    );
    await fetchAgents();
    return summarizeBulk(list.map(a => a.name), results);
  }, [fetchAgents]);

  // Bulk unregister: fire every delete simultaneously, refresh once at the end.
  const unregisterMany = useCallback(async (names: string[]): Promise<BulkResult> => {
    const results = await Promise.allSettled(
      names.map(n => apiDelete(`/api/agents/${encodeURIComponent(n)}`))
    );
    await fetchAgents();
    return summarizeBulk(names, results);
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

  return { fetchAgents, registerAgent, unregisterAgent, registerMany, unregisterMany, testAgent, diagnoseAgent, getSnapshot, getHealth, getHistory, getAudit };
}
