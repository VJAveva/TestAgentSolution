import { useCallback } from 'react';
import { apiFetch, apiFetchRaw, apiPost, apiPut } from '../lib/api';
import { useWatchListStore } from '../stores/watchlistStore';
import type { WatchListConfig } from '../types/api';

export function useWatchList() {
  const setConfig = useWatchListStore(s => s.setConfig);
  const setLoading = useWatchListStore(s => s.setLoading);
  const setError = useWatchListStore(s => s.setError);

  const fetchConfig = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const data = await apiFetch<WatchListConfig>('/api/watchlist');
      setConfig(data);
      return data;
    } catch (err: any) {
      const message = err?.status === 0
        ? 'Cannot reach backend server. Is it running on port 5050?'
        : err?.status
          ? `Failed to load watchlist: ${err.status}`
          : 'Failed to load watchlist';
      setError(message);
      console.error('fetchConfig error:', err);
      throw err;
    } finally {
      setLoading(false);
    }
  }, [setConfig, setLoading, setError]);

  const saveConfig = useCallback(async (config: WatchListConfig) => {
    const data = await apiPut<WatchListConfig>('/api/watchlist', config);
    setConfig(data);
    return data;
  }, [setConfig]);

  const refresh = useCallback(async () => {
    const data = await apiPost('/api/watchlist/refresh');
    await fetchConfig();
    return data;
  }, [fetchConfig]);

  const importXml = useCallback(async (xml: string) => {
    const data = await apiFetch('/api/watchlist/import', {
      method: 'POST',
      headers: { 'Content-Type': 'text/plain' },
      body: xml,
    });
    await fetchConfig();
    return data;
  }, [fetchConfig]);

  const exportXml = useCallback(async (tags?: string[]) => {
    const query = tags?.length ? `?tags=${encodeURIComponent(tags.join(','))}` : '';
    const response = await apiFetchRaw(`/api/watchlist/export${query}`);
    return response.text();
  }, []);

  return { fetchConfig, saveConfig, refresh, importXml, exportXml };
}
