import { create } from 'zustand';
import type { BuildSummary, BuildNode, TrendReport, ConsecutiveFailureAlert } from '../types/api';

interface ResultsState {
  builds: BuildSummary[];
  selectedBuild: BuildNode | null;
  trends: TrendReport | null;
  alerts: ConsecutiveFailureAlert[];
  setBuilds: (builds: BuildSummary[]) => void;
  setSelectedBuild: (build: BuildNode | null) => void;
  setTrends: (trends: TrendReport) => void;
  setAlerts: (alerts: ConsecutiveFailureAlert[]) => void;
}

export const useResultsStore = create<ResultsState>((set) => ({
  builds: [],
  selectedBuild: null,
  trends: null,
  alerts: [],
  setBuilds: (builds) => set({ builds }),
  setSelectedBuild: (build) => set({ selectedBuild: build }),
  setTrends: (trends) => set({ trends }),
  setAlerts: (alerts) => set({ alerts }),
}));
