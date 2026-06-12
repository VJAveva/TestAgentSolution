import { describe, it, expect, beforeEach } from 'vitest';
import { useLockStore, type PipelineLockDto } from './lockStore';

const mockLock: PipelineLockDto = {
  pipelineId: 'WarmSetup-Four-Nodes',
  ownerUserId: 'user-abc-123',
  ownerDisplayName: 'ravi.kumar',
  ownerClientKind: 'Web',
  acquiredUtc: '2026-06-12T10:00:00Z',
  expiresUtc: '2026-06-12T10:30:00Z',
};

const mockLock2: PipelineLockDto = {
  pipelineId: 'Sanity-Tests',
  ownerUserId: 'user-xyz-789',
  ownerDisplayName: 'vinod.kumar',
  ownerClientKind: 'Wpf',
  acquiredUtc: '2026-06-12T09:00:00Z',
  expiresUtc: '2026-06-12T09:30:00Z',
};

// Mock authStore to control currentUser for isLockedByOther tests
vi.mock('./authStore', () => ({
  useAuthStore: {
    getState: () => ({ user: { userId: 'user-abc-123' } }),
  },
}));

import { vi } from 'vitest';

describe('lockStore', () => {
  beforeEach(() => {
    useLockStore.setState({ locks: {} });
  });

  describe('onAcquired', () => {
    it('Should_UpsertLock_When_Acquired', () => {
      useLockStore.getState().onAcquired(mockLock);
      expect(useLockStore.getState().locks['WarmSetup-Four-Nodes']).toEqual(mockLock);
    });
  });

  describe('onReleased', () => {
    it('Should_RemoveLock_When_Released', () => {
      useLockStore.setState({ locks: { [mockLock.pipelineId]: mockLock } });
      useLockStore.getState().onReleased(mockLock);
      expect(useLockStore.getState().locks['WarmSetup-Four-Nodes']).toBeUndefined();
    });
  });

  describe('onExpired', () => {
    it('Should_RemoveLock_When_Expired', () => {
      useLockStore.setState({ locks: { [mockLock.pipelineId]: mockLock } });
      useLockStore.getState().onExpired(mockLock);
      expect(useLockStore.getState().locks['WarmSetup-Four-Nodes']).toBeUndefined();
    });
  });

  describe('onStolen', () => {
    it('Should_UpsertNewOwner_When_Stolen', () => {
      useLockStore.setState({ locks: { [mockLock.pipelineId]: mockLock } });
      const stolenLock = { ...mockLock, ownerUserId: 'new-owner', ownerDisplayName: 'admin.user' };
      useLockStore.getState().onStolen(stolenLock);
      expect(useLockStore.getState().locks['WarmSetup-Four-Nodes'].ownerUserId).toBe('new-owner');
    });
  });

  describe('onRewritten', () => {
    it('Should_UpsertLock_When_Rewritten', () => {
      useLockStore.setState({ locks: { [mockLock.pipelineId]: mockLock } });
      const rewritten = { ...mockLock, ownerDisplayName: 'admin.new' };
      useLockStore.getState().onRewritten(rewritten);
      expect(useLockStore.getState().locks['WarmSetup-Four-Nodes'].ownerDisplayName).toBe('admin.new');
    });
  });

  describe('setAll', () => {
    it('Should_ReplaceEntireMap_When_SetAll', () => {
      useLockStore.setState({ locks: { 'old-key': mockLock } });
      useLockStore.getState().setAll([mockLock, mockLock2]);
      const locks = useLockStore.getState().locks;
      expect(Object.keys(locks)).toHaveLength(2);
      expect(locks['WarmSetup-Four-Nodes']).toEqual(mockLock);
      expect(locks['Sanity-Tests']).toEqual(mockLock2);
    });

    it('Should_ClearMap_When_SetAllEmpty', () => {
      useLockStore.setState({ locks: { [mockLock.pipelineId]: mockLock } });
      useLockStore.getState().setAll([]);
      expect(Object.keys(useLockStore.getState().locks)).toHaveLength(0);
    });
  });

  describe('isLockedByOther', () => {
    it('Should_ReturnFalse_When_NoLockExists', () => {
      expect(useLockStore.getState().isLockedByOther('nonexistent')).toBe(false);
    });

    it('Should_ReturnFalse_When_OwnerMatchesCurrentUser', () => {
      // mockLock.ownerUserId = 'user-abc-123' matches mocked authStore
      useLockStore.setState({ locks: { [mockLock.pipelineId]: mockLock } });
      expect(useLockStore.getState().isLockedByOther('WarmSetup-Four-Nodes')).toBe(false);
    });

    it('Should_ReturnTrue_When_OwnerDiffersFromCurrentUser', () => {
      // mockLock2.ownerUserId = 'user-xyz-789' differs from mocked authStore
      useLockStore.setState({ locks: { [mockLock2.pipelineId]: mockLock2 } });
      expect(useLockStore.getState().isLockedByOther('Sanity-Tests')).toBe(true);
    });
  });

  describe('getLock', () => {
    it('Should_ReturnLock_When_Exists', () => {
      useLockStore.setState({ locks: { [mockLock.pipelineId]: mockLock } });
      expect(useLockStore.getState().getLock('WarmSetup-Four-Nodes')).toEqual(mockLock);
    });

    it('Should_ReturnUndefined_When_NotExists', () => {
      expect(useLockStore.getState().getLock('nonexistent')).toBeUndefined();
    });
  });
});
