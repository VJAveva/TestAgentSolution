import { useCallback } from 'react';
import axios from 'axios';
import { useExecutionStore } from '../stores/executionStore';
import type { ExecutionStatus } from '../types/api';

export function useExecution() {
  const setStatus = useExecutionStore(s => s.setStatus);

  const fetchStatus = useCallback(async () => {
    const { data } = await axios.get<ExecutionStatus>('/api/execution/status');
    setStatus(data.isExecuting, data.activeCount);
    return data;
  }, [setStatus]);

  const triggerAll = useCallback(async () => {
    const { data } = await axios.post('/api/execution/trigger-all');
    await fetchStatus();
    return data;
  }, [fetchStatus]);

  const triggerByTag = useCallback(async (tag: string) => {
    const { data } = await axios.post(`/api/execution/trigger/${encodeURIComponent(tag)}`);
    await fetchStatus();
    return data;
  }, [fetchStatus]);

  const triggerEvent = useCallback(async (tag: string, eventIndex: number) => {
    const { data } = await axios.post(`/api/execution/trigger-event/${encodeURIComponent(tag)}/${eventIndex}`);
    await fetchStatus();
    return data;
  }, [fetchStatus]);

  const cancelAll = useCallback(async () => {
    const { data } = await axios.post('/api/execution/cancel');
    await fetchStatus();
    return data;
  }, [fetchStatus]);

  const retrySession = useCallback(async (sessionId: string) => {
    const { data } = await axios.post(`/api/execution/retry/${encodeURIComponent(sessionId)}`);
    await fetchStatus();
    return data;
  }, [fetchStatus]);

  return { fetchStatus, triggerAll, triggerByTag, triggerEvent, cancelAll, retrySession };
}
