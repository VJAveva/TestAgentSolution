import { useCallback } from 'react';
import axios from 'axios';
import { useResultsStore } from '../stores/resultsStore';
import type { BuildSummary, BuildNode, TrendReport, ConsecutiveFailureAlert } from '../types/api';

export function useResults() {
  const setBuilds = useResultsStore(s => s.setBuilds);
  const setSelectedBuild = useResultsStore(s => s.setSelectedBuild);
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

  return { fetchBuilds, fetchBuild, fetchTrends, fetchAlerts, exportReport, sendReport };
}
