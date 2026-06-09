import { useCallback } from 'react';
import axios from 'axios';
import { useExecutionStore } from '../stores/executionStore';
import type { ExecutionStatus, SessionsResponse } from '../types/api';

export function useExecution() {
  const setStatus = useExecutionStore(s => s.setStatus);
  const setSessions = useExecutionStore(s => s.setSessions);

  const fetchStatus = useCallback(async () => {
    const { data } = await axios.get<ExecutionStatus>('/api/execution/proxy/status');
    setStatus(data.isExecuting, data.activeCount);
    return data;
  }, [setStatus]);

  const fetchSessions = useCallback(async () => {
    const { data } = await axios.get<SessionsResponse>('/api/execution/sessions');
    setStatus(data.hasActive, data.activeCount);
    setSessions(data.sessions ?? []);
    return data;
  }, [setStatus, setSessions]);

  const triggerAll = useCallback(async () => {
    const { data } = await axios.post('/api/execution/trigger-all');
    await fetchSessions();
    return data;
  }, [fetchSessions]);

  const triggerByTag = useCallback(async (
    tag: string,
    params?: { buildNumber?: string; dropLocation?: string; parameters?: Record<string, string>; lockVersion?: number }
  ) => {
    const { data } = await axios.post(`/api/execution/trigger/${encodeURIComponent(tag)}`, params);
    await fetchSessions();
    return data;
  }, [fetchSessions]);

  const triggerEvent = useCallback(async (tag: string, eventIndex: number) => {
    const { data } = await axios.post(`/api/execution/trigger-event/${encodeURIComponent(tag)}/${eventIndex}`);
    await fetchSessions();
    return data;
  }, [fetchSessions]);

  const cancelAll = useCallback(async () => {
    const { data } = await axios.post('/api/execution/cancel');
    await fetchSessions();
    return data;
  }, [fetchSessions]);

  const cancelSession = useCallback(async (sessionId: string) => {
    const { data } = await axios.post(`/api/execution/${encodeURIComponent(sessionId)}/cancel`);
    await fetchSessions();
    return data;
  }, [fetchSessions]);

  const retrySession = useCallback(async (sessionId: string) => {
    const { data } = await axios.post(`/api/execution/retry/${encodeURIComponent(sessionId)}`);
    await fetchSessions();
    return data;
  }, [fetchSessions]);

  return { fetchStatus, fetchSessions, triggerAll, triggerByTag, triggerEvent, cancelAll, cancelSession, retrySession };
}
