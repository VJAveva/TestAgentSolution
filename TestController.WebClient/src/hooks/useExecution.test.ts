import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';

// Mock the apiFetch layer (the hook calls apiPost/apiGet).
vi.mock('../lib/api', () => ({ apiGet: vi.fn(), apiPost: vi.fn() }));
import { apiGet, apiPost } from '../lib/api';
const mockPost = vi.mocked(apiPost);
const mockGet = vi.mocked(apiGet);

import { useExecution } from './useExecution';
import type { PipelineLockDto } from '../stores/lockStore';

const lock: PipelineLockDto = {
  pipelineId: 'Sanity',
  ownerUserId: 'user-1',
  ownerDisplayName: 'ravi.kumar',
  ownerClientKind: 'Web',
  acquiredUtc: '2026-06-19T10:00:00Z',
  expiresUtc: '2026-06-19T10:30:00Z',
};

function lockConflict(): any {
  return { status: 409, body: { lock } };
}

describe('useExecution — 409 lock-conflict handling', () => {
  let captured: PipelineLockDto | null;
  let listener: (e: Event) => void;

  beforeEach(() => {
    vi.clearAllMocks();
    captured = null;
    listener = (e: Event) => { captured = (e as CustomEvent).detail.lock; };
    window.addEventListener('pipeline-lock-conflict', listener);
    // fetchSessions (called after a successful trigger) needs a sessions payload.
    mockGet.mockResolvedValue({ activeCount: 0, hasActive: false, sessions: [] });
  });

  afterEach(() => {
    window.removeEventListener('pipeline-lock-conflict', listener);
  });

  it('Should_DispatchLockConflict_When_TriggerByTagReturns409', async () => {
    mockPost.mockRejectedValueOnce(lockConflict());
    const { result } = renderHook(() => useExecution());

    await act(async () => {
      await result.current.triggerByTag('Sanity');
    });

    expect(captured).toEqual(lock);
  });

  it('Should_NotRethrow_When_TriggerByTagReturns409', async () => {
    mockPost.mockRejectedValueOnce(lockConflict());
    const { result } = renderHook(() => useExecution());

    // Resolves (swallowed) rather than throwing.
    await act(async () => {
      await expect(result.current.triggerByTag('Sanity')).resolves.toBeUndefined();
    });
  });

  it('Should_NotDispatch_When_TriggerByTagSucceeds', async () => {
    mockPost.mockResolvedValueOnce({ ok: true });
    const { result } = renderHook(() => useExecution());

    await act(async () => {
      await result.current.triggerByTag('Sanity');
    });

    expect(captured).toBeNull();
    // After a successful trigger the hook refreshes sessions.
    expect(mockGet).toHaveBeenCalledWith('/api/execution/sessions');
  });

  it('Should_Rethrow_When_TriggerByTagFailsNon409', async () => {
    mockPost.mockRejectedValueOnce({ status: 500, body: {} });
    const { result } = renderHook(() => useExecution());

    await act(async () => {
      await expect(result.current.triggerByTag('Sanity')).rejects.toMatchObject({ status: 500 });
    });
    expect(captured).toBeNull();
  });

  it('Should_DispatchLockConflict_When_TriggerEventReturns409', async () => {
    mockPost.mockRejectedValueOnce(lockConflict());
    const { result } = renderHook(() => useExecution());

    await act(async () => {
      await result.current.triggerEvent('Sanity', 0);
    });

    expect(captured).toEqual(lock);
  });
});
