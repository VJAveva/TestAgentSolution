import { create } from 'zustand';
import type { LogEntry } from '../types/api';

interface ExecutionState {
  isExecuting: boolean;
  activeCount: number;
  logs: LogEntry[];
  maxLogs: number;
  isLogPaused: boolean;
  setStatus: (isExecuting: boolean, activeCount: number) => void;
  addLog: (entry: LogEntry) => void;
  clearLogs: () => void;
  togglePause: () => void;
}

export const useExecutionStore = create<ExecutionState>((set) => ({
  isExecuting: false,
  activeCount: 0,
  logs: [],
  maxLogs: 2000,
  isLogPaused: false,
  setStatus: (isExecuting, activeCount) => set({ isExecuting, activeCount }),
  addLog: (entry) =>
    set((s) => {
      if (s.isLogPaused) return s;
      const logs = [...s.logs, entry];
      if (logs.length > s.maxLogs) logs.splice(0, logs.length - s.maxLogs);
      return { logs };
    }),
  clearLogs: () => set({ logs: [] }),
  togglePause: () => set((s) => ({ isLogPaused: !s.isLogPaused })),
}));
