import { create } from 'zustand';
import type {
  ConsolidatedImpact,
  RegressionScope,
  RegressionSyncStatus,
  RegressionConnectionInfo,
  RegressionComponentRef,
  ChurnSummary,
} from '../types/api';

interface RegressionState {
  consolidated: ConsolidatedImpact | null;
  scope: RegressionScope | null;
  syncStatus: RegressionSyncStatus | null;
  connection: RegressionConnectionInfo | null;
  summary: ChurnSummary | null;
  branches: string[];
  components: RegressionComponentRef[];
  showRuntime: boolean;
  showConfig: boolean;
  hideAutomated: boolean;
  filterBug: boolean;
  filterStory: boolean;
  filterIms: boolean;
  loading: boolean;
  error: string | null;
  setConsolidated: (v: ConsolidatedImpact | null) => void;
  setScope: (v: RegressionScope | null) => void;
  setSyncStatus: (v: RegressionSyncStatus | null) => void;
  setConnection: (v: RegressionConnectionInfo | null) => void;
  setSummary: (v: ChurnSummary | null) => void;
  setBranches: (v: string[]) => void;
  setComponents: (v: RegressionComponentRef[]) => void;
  setShowRuntime: (v: boolean) => void;
  setShowConfig: (v: boolean) => void;
  setHideAutomated: (v: boolean) => void;
  setFilterBug: (v: boolean) => void;
  setFilterStory: (v: boolean) => void;
  setFilterIms: (v: boolean) => void;
  setLoading: (v: boolean) => void;
  setError: (v: string | null) => void;
}

export const useRegressionStore = create<RegressionState>((set) => ({
  consolidated: null,
  scope: null,
  syncStatus: null,
  connection: null,
  summary: null,
  branches: [],
  components: [],
  showRuntime: true,
  showConfig: true,
  hideAutomated: true,
  filterBug: false,
  filterStory: false,
  filterIms: false,
  loading: false,
  error: null,
  setConsolidated: (consolidated) => set({ consolidated }),
  setScope: (scope) => set({ scope }),
  setSyncStatus: (syncStatus) => set({ syncStatus }),
  setConnection: (connection) => set({ connection }),
  setSummary: (summary) => set({ summary }),
  setBranches: (branches) => set({ branches }),
  setComponents: (components) => set({ components }),
  setShowRuntime: (showRuntime) => set({ showRuntime }),
  setShowConfig: (showConfig) => set({ showConfig }),
  setHideAutomated: (hideAutomated) => set({ hideAutomated }),
  setFilterBug: (filterBug) => set({ filterBug }),
  setFilterStory: (filterStory) => set({ filterStory }),
  setFilterIms: (filterIms) => set({ filterIms }),
  setLoading: (loading) => set({ loading }),
  setError: (error) => set({ error }),
}));
