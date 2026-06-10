import { create } from 'zustand';
import { apiFetch } from '../lib/api';

export type SystemMode = 'default' | 'secured';

interface SystemModeState {
  mode: SystemMode;
  isDefault: boolean;
  isSecured: boolean;
  isLoading: boolean;
  fetchMode: () => Promise<void>;
  setMode: (mode: SystemMode) => void;
  onModeChanged: (mode: SystemMode) => void;
}

export const useSystemModeStore = create<SystemModeState>((set, get) => ({
  mode: 'default',
  isDefault: true,
  isSecured: false,
  isLoading: false,

  fetchMode: async () => {
    set({ isLoading: true });
    try {
      const data = await apiFetch<{ mode: SystemMode; enabled: boolean }>('/api/system/mode');
      const mode = data.mode;
      set({
        mode,
        isDefault: mode === 'default',
        isSecured: mode === 'secured',
        isLoading: false,
      });
    } catch {
      set({ isLoading: false });
    }
  },

  setMode: (mode) => {
    set({
      mode,
      isDefault: mode === 'default',
      isSecured: mode === 'secured',
    });
  },

  onModeChanged: (mode) => {
    const current = get().mode;
    if (current === mode) return;
    set({
      mode,
      isDefault: mode === 'default',
      isSecured: mode === 'secured',
    });
    // Reload the page to reflect mode change (login screen if secured, main view if default)
    window.location.reload();
  },
}));
