import { useCallback } from 'react';
import { apiFetch, apiFetchRaw } from '../lib/api';
import { useResultsStore } from '../stores/resultsStore';
import type { BuildSummary, BuildNode, BuildDetailResponse, TrendReport, ConsecutiveFailureAlert } from '../types/api';

export function useResults() {
  const setBuilds = useResultsStore(s => s.setBuilds);
  const setSelectedBuild = useResultsStore(s => s.setSelectedBuild);
  const setBuildDetail = useResultsStore(s => s.setBuildDetail);
  const setTrends = useResultsStore(s => s.setTrends);
  const setAlerts = useResultsStore(s => s.setAlerts);

  const fetchBuilds = useCallback(async () => {
    const data = await apiFetch<{ items: BuildSummary[]; totalCount: number; page: number; pageSize: number }>('/api/results/builds');
    setBuilds(data.items ?? []);
    return data.items ?? [];
  }, [setBuilds]);

  const fetchBuild = useCallback(async (buildNumber: string) => {
    const data = await apiFetch<BuildNode>(`/api/results/builds/${encodeURIComponent(buildNumber)}`);
    setSelectedBuild(data);
    return data;
  }, [setSelectedBuild]);

  const fetchBuildDetail = useCallback(async (buildNumber: string, params?: { outcome?: string; useCase?: string; search?: string }) => {
    const query = params ? '?' + new URLSearchParams(
      Object.entries(params).filter(([, v]) => v != null) as [string, string][]
    ).toString() : '';
    const data = await apiFetch<BuildDetailResponse>(`/api/results/builds/${encodeURIComponent(buildNumber)}/detail${query}`);
    setBuildDetail(data);
    return data;
  }, [setBuildDetail]);

  const fetchTrends = useCallback(async () => {
    const data = await apiFetch<TrendReport>('/api/results/trends');
    setTrends(data);
    return data;
  }, [setTrends]);

  const fetchAlerts = useCallback(async () => {
    const data = await apiFetch<ConsecutiveFailureAlert[]>('/api/results/alerts');
    setAlerts(data);
    return data;
  }, [setAlerts]);

  const exportReport = useCallback(async (buildNumber: string, format: 'html' | 'csv' = 'html') => {
    // Blob downloads bypass JSON parsing via apiFetchRaw, which still carries
    // the auth token + correlation headers and respects VITE_API_BASE_URL.
    const response = await apiFetchRaw(
      `/api/results/export/${encodeURIComponent(buildNumber)}?format=${format}`);
    const blob = await response.blob();
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `BuildResults_${buildNumber}.${format}`;
    a.click();
    URL.revokeObjectURL(url);
  }, []);

  const sendReport = useCallback(async (buildNumber: string, recipients?: string) => {
    return apiFetch<{ message: string; recipients: number }>('/api/results/send-report', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ buildNumber, recipients }),
    });
  }, []);

  return { fetchBuilds, fetchBuild, fetchBuildDetail, fetchTrends, fetchAlerts, exportReport, sendReport };
}
