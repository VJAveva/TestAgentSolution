import { create } from 'zustand';
import type {
  ConsolidatedImpact,
  RegressionScope,
  RegressionSyncStatus,
  RegressionConnectionInfo,
  RegressionComponentRef,
  ChurnSummary,
  ImpactIndexHealth,
} from '../types/api';

interface RegressionState {
  consolidated: ConsolidatedImpact | null;
  scope: RegressionScope | null;
  syncStatus: RegressionSyncStatus | null;
  connection: RegressionConnectionInfo | null;
  summary: ChurnSummary | null;
  indexHealth: ImpactIndexHealth | null;
  branches: string[];
  components: RegressionComponentRef[];
  showRuntime: boolean;
  showConfig: boolean;
  hideAutomated: boolean;
  showAllChanges: boolean;
  filterBug: boolean;
  filterStory: boolean;
  filterIms: boolean;
  loading: boolean;
  aiLoading: boolean;
  error: string | null;
  setConsolidated: (v: ConsolidatedImpact | null) => void;
  setScope: (v: RegressionScope | null) => void;
  setSyncStatus: (v: RegressionSyncStatus | null) => void;
  setConnection: (v: RegressionConnectionInfo | null) => void;
  setSummary: (v: ChurnSummary | null) => void;
  setIndexHealth: (v: ImpactIndexHealth | null) => void;
  setBranches: (v: string[]) => void;
  setComponents: (v: RegressionComponentRef[]) => void;
  setShowRuntime: (v: boolean) => void;
  setShowConfig: (v: boolean) => void;
  setHideAutomated: (v: boolean) => void;
  setShowAllChanges: (v: boolean) => void;
  setFilterBug: (v: boolean) => void;
  setFilterStory: (v: boolean) => void;
  setFilterIms: (v: boolean) => void;
  setLoading: (v: boolean) => void;
  setAiLoading: (v: boolean) => void;
  setError: (v: string | null) => void;
}

export const useRegressionStore = create<RegressionState>((set) => ({
  consolidated: null,
  scope: null,
  syncStatus: null,
  connection: null,
  summary: null,
  indexHealth: null,
  branches: [],
  components: [],
  showRuntime: true,
  showConfig: true,
  hideAutomated: true,
  showAllChanges: false,
  filterBug: false,
  filterStory: false,
  filterIms: false,
  loading: false,
  aiLoading: false,
  error: null,
  setConsolidated: (consolidated) => set({ consolidated }),
  setScope: (scope) => set({ scope }),
  setSyncStatus: (syncStatus) => set({ syncStatus }),
  setConnection: (connection) => set({ connection }),
  setSummary: (summary) => set({ summary }),
  setIndexHealth: (indexHealth) => set({ indexHealth }),
  setAiLoading: (aiLoading) => set({ aiLoading }),
  setBranches: (branches) => set({ branches }),
  setComponents: (components) => set({ components }),
  setShowRuntime: (showRuntime) => set({ showRuntime }),
  setShowConfig: (showConfig) => set({ showConfig }),
  setHideAutomated: (hideAutomated) => set({ hideAutomated }),
  setShowAllChanges: (showAllChanges) => set({ showAllChanges }),
  setFilterBug: (filterBug) => set({ filterBug }),
  setFilterStory: (filterStory) => set({ filterStory }),
  setFilterIms: (filterIms) => set({ filterIms }),
  setLoading: (loading) => set({ loading }),
  setError: (error) => set({ error }),
}));
