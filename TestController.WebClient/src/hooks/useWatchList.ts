import { useCallback } from 'react';
import axios from 'axios';
import { useWatchListStore } from '../stores/watchlistStore';
import type { WatchListConfig } from '../types/api';

export function useWatchList() {
  const setConfig = useWatchListStore(s => s.setConfig);

  const fetchConfig = useCallback(async () => {
    const { data } = await axios.get<WatchListConfig>('/api/watchlist');
    setConfig(data);
    return data;
  }, [setConfig]);

  const saveConfig = useCallback(async (config: WatchListConfig) => {
    const { data } = await axios.put('/api/watchlist', config);
    return data;
  }, []);

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
