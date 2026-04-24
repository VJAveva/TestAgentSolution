import { create } from 'zustand';
import type { BuildSummary, BuildNode, BuildDetailResponse, TrendReport, ConsecutiveFailureAlert } from '../types/api';

interface ResultsState {
  builds: BuildSummary[];
  selectedBuild: BuildNode | null;
  buildDetail: BuildDetailResponse | null;
  trends: TrendReport | null;
  alerts: ConsecutiveFailureAlert[];
  setBuilds: (builds: BuildSummary[]) => void;
  setSelectedBuild: (build: BuildNode | null) => void;
  setBuildDetail: (detail: BuildDetailResponse | null) => void;
  setTrends: (trends: TrendReport) => void;
  setAlerts: (alerts: ConsecutiveFailureAlert[]) => void;
}

export const useResultsStore = create<ResultsState>((set) => ({
  builds: [],
  selectedBuild: null,
  buildDetail: null,
  trends: null,
  alerts: [],
  setBuilds: (builds) => set({ builds }),
  setSelectedBuild: (build) => set({ selectedBuild: build }),
  setBuildDetail: (detail) => set({ buildDetail: detail }),
  setTrends: (trends) => set({ trends }),
  setAlerts: (alerts) => set({ alerts }),
}));
