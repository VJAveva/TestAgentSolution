import { useCallback } from 'react';
import axios from 'axios';
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
      const { data } = await axios.get<WatchListConfig>('/api/watchlist');
      setConfig(data);
      return data;
    } catch (err) {
      const message = axios.isAxiosError(err)
        ? err.code === 'ERR_NETWORK'
          ? 'Cannot reach backend server. Is it running on port 5050?'
          : `Failed to load watchlist: ${err.response?.status ?? err.message}`
        : 'Failed to load watchlist';
      setError(message);
      console.error('fetchConfig error:', err);
      throw err;
    } finally {
      setLoading(false);
    }
  }, [setConfig, setLoading, setError]);

  const saveConfig = useCallback(async (config: WatchListConfig) => {
    const { data } = await axios.put<WatchListConfig>('/api/watchlist', config);
    setConfig(data);
    return data;
  }, [setConfig]);

  const refresh = useCallback(async () => {
    const { data } = await axios.post('/api/watchlist/refresh');
    await fetchConfig();
    return data;
  }, [fetchConfig]);

  const importXml = useCallback(async (xml: string) => {
    const { data } = await axios.post('/api/watchlist/import', xml, {
      headers: { 'Content-Type': 'text/plain' },
    });
    await fetchConfig();
    return data;
  }, [fetchConfig]);

  const exportXml = useCallback(async (tags?: string[]) => {
    const params = tags?.length ? { tags: tags.join(',') } : {};
    const { data } = await axios.get<string>('/api/watchlist/export', { params, responseType: 'text' as const });
    return data;
  }, []);

  return { fetchConfig, saveConfig, refresh, importXml, exportXml };
}
