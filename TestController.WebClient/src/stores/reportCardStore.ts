import { create } from 'zustand';
import type { BuildReportCard } from '../types/api';

interface ReportCardState {
  card: BuildReportCard | null;
  builds: string[];
  selectedBuild: string | null;
  loading: boolean;
  error: string | null;
  setCard: (card: BuildReportCard | null) => void;
  setBuilds: (builds: string[]) => void;
  setSelectedBuild: (build: string | null) => void;
  setLoading: (loading: boolean) => void;
  setError: (error: string | null) => void;
}

export const useReportCardStore = create<ReportCardState>((set) => ({
  card: null,
  builds: [],
  selectedBuild: null,
  loading: false,
  error: null,
  setCard: (card) => set({ card }),
  setBuilds: (builds) => set({ builds }),
  setSelectedBuild: (selectedBuild) => set({ selectedBuild }),
  setLoading: (loading) => set({ loading }),
  setError: (error) => set({ error }),
}));
