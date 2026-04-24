import { useCallback } from 'react';
import axios from 'axios';
import { useResultsStore } from '../stores/resultsStore';
import type { BuildSummary, BuildNode, BuildDetailResponse, TrendReport, ConsecutiveFailureAlert } from '../types/api';

export function useResults() {
  const setBuilds = useResultsStore(s => s.setBuilds);
  const setSelectedBuild = useResultsStore(s => s.setSelectedBuild);
  const setBuildDetail = useResultsStore(s => s.setBuildDetail);
  const setTrends = useResultsStore(s => s.setTrends);
  const setAlerts = useResultsStore(s => s.setAlerts);

  const fetchBuilds = useCallback(async () => {
    const { data } = await axios.get<BuildSummary[]>('/api/results/builds');
    setBuilds(data);
    return data;
  }, [setBuilds]);

  const fetchBuild = useCallback(async (buildNumber: string) => {
    const { data } = await axios.get<BuildNode>(`/api/results/builds/${encodeURIComponent(buildNumber)}`);
    setSelectedBuild(data);
    return data;
  }, [setSelectedBuild]);

  const fetchBuildDetail = useCallback(async (buildNumber: string, params?: { outcome?: string; useCase?: string; search?: string }) => {
    const { data } = await axios.get<BuildDetailResponse>(`/api/results/builds/${encodeURIComponent(buildNumber)}/detail`, { params });
    setBuildDetail(data);
    return data;
  }, [setBuildDetail]);

  const fetchTrends = useCallback(async () => {
    const { data } = await axios.get<TrendReport>('/api/results/trends');
    setTrends(data);
    return data;
  }, [setTrends]);

  const fetchAlerts = useCallback(async () => {
    const { data } = await axios.get<ConsecutiveFailureAlert[]>('/api/results/alerts');
    setAlerts(data);
    return data;
  }, [setAlerts]);

  const exportReport = useCallback(async (buildNumber: string, format: 'html' | 'csv' = 'html') => {
    const { data } = await axios.get(`/api/results/export/${encodeURIComponent(buildNumber)}`, {
      params: { format },
      responseType: 'blob',
    });
    const url = URL.createObjectURL(data);
    const a = document.createElement('a');
    a.href = url;
    a.download = `BuildResults_${buildNumber}.${format}`;
    a.click();
    URL.revokeObjectURL(url);
  }, []);

  const sendReport = useCallback(async (buildNumber: string, recipients?: string) => {
    const { data } = await axios.post('/api/results/send-report', { buildNumber, recipients });
    return data;
  }, []);

  return { fetchBuilds, fetchBuild, fetchBuildDetail, fetchTrends, fetchAlerts, exportReport, sendReport };
}
