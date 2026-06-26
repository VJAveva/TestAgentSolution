import { useCallback } from 'react';
import { apiFetch } from '../lib/api';
import { useReportCardStore } from '../stores/reportCardStore';
import type { BuildReportCard } from '../types/api';

export function useReportCard() {
  const setCard = useReportCardStore(s => s.setCard);
  const setBuilds = useReportCardStore(s => s.setBuilds);
  const setSelectedBuild = useReportCardStore(s => s.setSelectedBuild);
  const setLoading = useReportCardStore(s => s.setLoading);
  const setError = useReportCardStore(s => s.setError);

  const fetchBuilds = useCallback(async () => {
    const data = await apiFetch<{ items: string[] }>('/api/reportcard/builds');
    setBuilds(data.items ?? []);
    return data.items ?? [];
  }, [setBuilds]);

  const fetchCard = useCallback(async (buildNumber?: string) => {
    setLoading(true);
    setError(null);
    try {
      const query = buildNumber ? `?build=${encodeURIComponent(buildNumber)}` : '';
      const data = await apiFetch<BuildReportCard>(`/api/reportcard${query}`);
      setCard(data);
      setSelectedBuild(data.buildNumber || buildNumber || null);
      return data;
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load report card');
      setCard(null);
      return null;
    } finally {
      setLoading(false);
    }
  }, [setCard, setSelectedBuild, setLoading, setError]);

  return { fetchBuilds, fetchCard };
}
