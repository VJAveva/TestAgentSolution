import { create } from 'zustand';
import { useAuthStore } from './authStore';

export interface PipelineLockDto {
  pipelineId: string;
  ownerUserId: string;
  ownerDisplayName: string;
  ownerClientKind: string;
  acquiredUtc: string;
  expiresUtc: string;
}

interface LockState {
  locks: Record<string, PipelineLockDto>;

  onAcquired: (dto: PipelineLockDto) => void;
  onReleased: (dto: PipelineLockDto) => void;
  onExpired: (dto: PipelineLockDto) => void;
  onStolen: (dto: PipelineLockDto) => void;
  onRewritten: (dto: PipelineLockDto) => void;
  setAll: (dtos: PipelineLockDto[]) => void;
  getLock: (tag: string) => PipelineLockDto | undefined;
  isLockedByOther: (tag: string) => boolean;
}

export const useLockStore = create<LockState>((set, get) => ({
  locks: {},

  onAcquired: (dto) => set((s) => ({ locks: { ...s.locks, [dto.pipelineId]: dto } })),

  onReleased: (dto) => set((s) => {
    const { [dto.pipelineId]: _, ...rest } = s.locks;
    return { locks: rest };
  }),

  onExpired: (dto) => set((s) => {
    const { [dto.pipelineId]: _, ...rest } = s.locks;
    return { locks: rest };
  }),

  onStolen: (dto) => set((s) => ({ locks: { ...s.locks, [dto.pipelineId]: dto } })),

  onRewritten: (dto) => set((s) => ({ locks: { ...s.locks, [dto.pipelineId]: dto } })),

  setAll: (dtos) => {
    const map: Record<string, PipelineLockDto> = {};
    for (const dto of dtos) {
      map[dto.pipelineId] = dto;
    }
    set({ locks: map });
  },

  getLock: (tag) => get().locks[tag],

  isLockedByOther: (tag) => {
    const lock = get().locks[tag];
    if (!lock) return false;
    const currentUserId = useAuthStore.getState().user?.userId;
    return lock.ownerUserId !== currentUserId;
  },
}));
