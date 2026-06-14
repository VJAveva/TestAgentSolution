import { useCallback } from 'react';
import axios from 'axios';
import { useExecutionStore } from '../stores/executionStore';
import type { ExecutionStatus, SessionsResponse } from '../types/api';
import type { PipelineLockDto } from '../stores/lockStore';

/** Dispatched when a trigger call returns 409 with a PipelineLockDto body. */
export function dispatchLockConflict(lock: PipelineLockDto): void {
  window.dispatchEvent(new CustomEvent('pipeline-lock-conflict', { detail: { lock } }));
}

function extractLockFrom409(error: any): PipelineLockDto | null {
  if (error?.response?.status === 409) {
    const body = error.response.data;
    if (body?.lock) return body.lock as PipelineLockDto;
  }
  return null;
}

export function useExecution() {
  const setStatus = useExecutionStore(s => s.setStatus);
  const setSessions = useExecutionStore(s => s.setSessions);

  const fetchStatus = useCallback(async () => {
    const { data } = await axios.get<ExecutionStatus>('/api/execution/status');
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
    try {
      const { data } = await axios.post(`/api/execution/trigger/${encodeURIComponent(tag)}`, params);
      await fetchSessions();
      return data;
    } catch (err: any) {
      const lock = extractLockFrom409(err);
      if (lock) {
        dispatchLockConflict(lock);
        return;
      }
      throw err;
    }
  }, [fetchSessions]);

  const triggerEvent = useCallback(async (tag: string, eventIndex: number) => {
    try {
      const { data } = await axios.post(`/api/execution/trigger-event/${encodeURIComponent(tag)}/${eventIndex}`);
      await fetchSessions();
      return data;
    } catch (err: any) {
      const lock = extractLockFrom409(err);
      if (lock) {
        dispatchLockConflict(lock);
        return;
      }
      throw err;
    }
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
