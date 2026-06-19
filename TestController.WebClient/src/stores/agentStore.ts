import { create } from 'zustand';
import type { AgentInfo } from '../types/api';

interface AgentState {
  agents: AgentInfo[];
  selectedAgent: string | null;
  setAgents: (agents: AgentInfo[]) => void;
  selectAgent: (name: string | null) => void;
  updateStatus: (name: string, status: string) => void;
  applyHeartbeats: (updates: { name: string; status: string }[]) => void;
  addAgent: (agent: AgentInfo) => void;
  removeAgent: (name: string) => void;
}

export const useAgentStore = create<AgentState>((set) => ({
  agents: [],
  selectedAgent: null,
  setAgents: (agents) => set({ agents }),
  selectAgent: (name) => set({ selectedAgent: name }),
  updateStatus: (name, status) =>
    set((s) => ({
      agents: s.agents.map(a =>
        a.name.toLowerCase() === name.toLowerCase()
          ? { ...a, status, lastCheckedUtc: new Date().toISOString() }
          : a
      ),
    })),
  applyHeartbeats: (updates) =>
    set((s) => {
      if (updates.length === 0) return s;
      const map = new Map(updates.map(u => [u.name.toLowerCase(), u.status]));
      const now = new Date().toISOString();
      return {
        agents: s.agents.map(a => {
          const status = map.get(a.name.toLowerCase());
          return status ? { ...a, status, lastCheckedUtc: now } : a;
        }),
      };
    }),
  addAgent: (agent) => set((s) => ({ agents: [...s.agents, agent] })),
  removeAgent: (name) =>
    set((s) => ({
      agents: s.agents.filter(a => a.name.toLowerCase() !== name.toLowerCase()),
      selectedAgent: s.selectedAgent?.toLowerCase() === name.toLowerCase() ? null : s.selectedAgent,
    })),
}));
