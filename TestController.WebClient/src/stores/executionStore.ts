import { create } from 'zustand';
import type { LogEntry, SessionInfo } from '../types/api';

interface ExecutionState {
  isExecuting: boolean;
  activeCount: number;
  sessions: SessionInfo[];
  logs: LogEntry[];
  maxLogs: number;
  isLogPaused: boolean;
  logSessionFilter: string;
  setStatus: (isExecuting: boolean, activeCount: number) => void;
  setSessions: (sessions: SessionInfo[]) => void;
  addLog: (entry: LogEntry) => void;
  clearLogs: () => void;
  togglePause: () => void;
  setLogSessionFilter: (sessionId: string) => void;
}

export const useExecutionStore = create<ExecutionState>((set) => ({
  isExecuting: false,
  activeCount: 0,
  sessions: [],
  logs: [],
  maxLogs: 2000,
  isLogPaused: false,
  logSessionFilter: '',
  setStatus: (isExecuting, activeCount) => set({ isExecuting, activeCount }),
  setSessions: (sessions) => set({ sessions }),
  addLog: (entry) =>
    set((s) => {
      if (s.isLogPaused) return s;
      const logs = [...s.logs, entry];
      if (logs.length > s.maxLogs) logs.splice(0, logs.length - s.maxLogs);
      return { logs };
    }),
  clearLogs: () => set({ logs: [] }),
  togglePause: () => set((s) => ({ isLogPaused: !s.isLogPaused })),
  setLogSessionFilter: (sessionId) => set({ logSessionFilter: sessionId }),
}));
