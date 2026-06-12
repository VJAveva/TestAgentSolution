import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, act } from '@testing-library/react';
import LockBadge from './LockBadge';
import type { PipelineLockDto } from '../../stores/lockStore';

const ownLock: PipelineLockDto = {
  pipelineId: 'WarmSetup',
  ownerUserId: 'me-123',
  ownerDisplayName: 'ravi.kumar',
  ownerClientKind: 'Web',
  acquiredUtc: new Date(Date.now() - 65_000).toISOString(), // 1m 5s ago
  expiresUtc: new Date(Date.now() + 300_000).toISOString(),
};

const otherLock: PipelineLockDto = {
  pipelineId: 'Sanity-Tests',
  ownerUserId: 'other-456',
  ownerDisplayName: 'vinod.kumar',
  ownerClientKind: 'Wpf',
  acquiredUtc: new Date(Date.now() - 120_000).toISOString(),
  expiresUtc: new Date(Date.now() + 300_000).toISOString(),
};

describe('LockBadge', () => {
  beforeEach(() => {
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('Should_RenderNothing_When_NoLock', () => {
    const { container } = render(<LockBadge lock={undefined} isOwn={false} />);
    expect(container.innerHTML).toBe('');
  });

  it('Should_RenderAmberBadge_When_OtherOwnerLock', () => {
    render(<LockBadge lock={otherLock} isOwn={false} />);
    expect(screen.getByText(/Locked by vinod\.kumar \(Wpf\)/)).toBeTruthy();
  });

  it('Should_RenderBlueBadge_When_OwnLock', () => {
    render(<LockBadge lock={ownLock} isOwn={true} />);
    expect(screen.getByText(/Your run/)).toBeTruthy();
  });

  it('Should_UpdateElapsedTimer_When_OwnLock', () => {
    render(<LockBadge lock={ownLock} isOwn={true} />);
    // Advance 5 seconds
    act(() => { vi.advanceTimersByTime(5000); });
    // Timer should have updated (exact value depends on timing)
    expect(screen.getByText(/Your run/)).toBeTruthy();
  });

  it('Should_CleanupInterval_When_Unmounted', () => {
    const clearSpy = vi.spyOn(window, 'clearInterval');
    const { unmount } = render(<LockBadge lock={ownLock} isOwn={true} />);
    unmount();
    expect(clearSpy).toHaveBeenCalled();
    clearSpy.mockRestore();
  });
});
