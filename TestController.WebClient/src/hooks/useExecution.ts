import { useCallback } from 'react';
import { apiGet, apiPost } from '../lib/api';
import { useExecutionStore } from '../stores/executionStore';
import type { ExecutionStatus, SessionsResponse } from '../types/api';
import type { PipelineLockDto } from '../stores/lockStore';

/** Dispatched when a trigger call returns 409 with a PipelineLockDto body. */
export function dispatchLockConflict(lock: PipelineLockDto): void {
  window.dispatchEvent(new CustomEvent('pipeline-lock-conflict', { detail: { lock } }));
}

function extractLockFrom409(error: any): PipelineLockDto | null {
  if (error?.status === 409 && error.body?.lock) {
    return error.body.lock as PipelineLockDto;
  }
  return null;
}

export function useExecution() {
  const setStatus = useExecutionStore(s => s.setStatus);
  const setSessions = useExecutionStore(s => s.setSessions);

  const fetchStatus = useCallback(async () => {
    const data = await apiGet<ExecutionStatus>('/api/execution/status');
    setStatus(data.isExecuting, data.activeCount);
    return data;
  }, [setStatus]);

  const fetchSessions = useCallback(async () => {
    const data = await apiGet<SessionsResponse>('/api/execution/sessions');
    setStatus(data.hasActive, data.activeCount);
    setSessions(data.sessions ?? []);
    return data;
  }, [setStatus, setSessions]);

  const triggerAll = useCallback(async () => {
    const data = await apiPost('/api/execution/trigger-all');
    await fetchSessions();
    return data;
  }, [fetchSessions]);

  const triggerByTag = useCallback(async (
    tag: string,
    params?: { buildNumber?: string; dropLocation?: string; parameters?: Record<string, string>; lockVersion?: number }
  ) => {
    try {
      const data = await apiPost(`/api/execution/trigger/${encodeURIComponent(tag)}`, params);
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
      const data = await apiPost(`/api/execution/trigger-event/${encodeURIComponent(tag)}/${eventIndex}`);
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
    const data = await apiPost('/api/execution/cancel');
    await fetchSessions();
    return data;
  }, [fetchSessions]);

  const cancelSession = useCallback(async (sessionId: string) => {
    const data = await apiPost(`/api/execution/${encodeURIComponent(sessionId)}/cancel`);
    await fetchSessions();
    return data;
  }, [fetchSessions]);

  const retrySession = useCallback(async (sessionId: string) => {
    const data = await apiPost(`/api/execution/retry/${encodeURIComponent(sessionId)}`);
    await fetchSessions();
    return data;
  }, [fetchSessions]);

  /** RBAC-gated retry: authorizes via pipelines controller, then triggers execution retry. */
  const retryByTag = useCallback(async (tag: string) => {
    try {
      // Phase 4: authorize + acquire lock via RBAC endpoint
      await apiPost(`/api/pipelines/${encodeURIComponent(tag)}/retry`);
      // Delegate to existing retry-by-tag execution endpoint
      const data = await apiPost(`/api/execution/retry-tag/${encodeURIComponent(tag)}`);
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

  return { fetchStatus, fetchSessions, triggerAll, triggerByTag, triggerEvent, cancelAll, cancelSession, retrySession, retryByTag };
}
