import { create } from 'zustand';
import { apiFetch } from '../lib/api';

export type FeatureKey =
  | 'Results' | 'FirReport' | 'CodeChurn' | 'ImpactMapping'
  | 'PipelineBuilder' | 'GlobalVariables' | 'Reports';

export interface FeatureInfo {
  key: FeatureKey;
  enabled: boolean;
  configuredOn: boolean;
  offBecause: string | null;
  description: string;
  dependsOn: FeatureKey | null;
}

export interface CodeChurnRepository {
  name: string;
  url: string | null;
  branches: string[];
  includePaths: string[];
  excludePaths: string[];
  /** Environment variable NAME on the controller; the value is never sent to the client. */
  credentialRef: string | null;
}

export interface CodeChurnConfig {
  /** False when Features.json has no CodeChurn section: the component map decides. */
  configured: boolean;
  repositories: CodeChurnRepository[];
}

interface FeaturesResponse {
  teamName: string;
  features: FeatureInfo[];
  map: Partial<Record<FeatureKey, boolean>>;
  codeChurn?: CodeChurnConfig;
}

interface FeatureState {
  teamName: string;
  features: FeatureInfo[];
  map: Partial<Record<FeatureKey, boolean>>;
  codeChurn: CodeChurnConfig;
  loaded: boolean;
  fetchFeatures: () => Promise<void>;
}

export const useFeatureStore = create<FeatureState>((set) => ({
  teamName: '',
  features: [],
  map: {},
  codeChurn: { configured: false, repositories: [] },
  loaded: false,

  fetchFeatures: async () => {
    try {
      const data = await apiFetch<FeaturesResponse>('/api/features');
      set({
        teamName: data.teamName,
        features: data.features,
        map: data.map ?? {},
        codeChurn: data.codeChurn ?? { configured: false, repositories: [] },
        loaded: true,
      });
    } catch {
      // An older controller has no /api/features: keep everything on rather than hiding real features.
      set({ loaded: true });
    }
  },
}));

/** Unknown or not-yet-loaded features count as on, matching the server's default. */
export function isFeatureOn(map: Partial<Record<FeatureKey, boolean>>, key: FeatureKey): boolean {
  return map[key] !== false;
}

export function useFeature(key: FeatureKey): boolean {
  return useFeatureStore((s) => s.map[key] !== false);
}
