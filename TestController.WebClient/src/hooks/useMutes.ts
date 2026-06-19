import { useCallback, useEffect, useState } from 'react';
import { apiFetch } from '../lib/api';
import { useCan } from './useCapabilities';

export interface MuteItem {
  muteId: number;
  target: string;
  targetType: string;
  mutedByUserId: string;
  mutedAtUtc: string;
  expiresAtUtc: string | null;
}

export function useMutes() {
  const [mutes, setMutes] = useState<MuteItem[]>([]);
  const canMute = useCan('Notification_Mute');

  const fetchMutes = useCallback(async () => {
    try {
      const data = await apiFetch<MuteItem[]>('/api/notifications/mutes');
      setMutes(data);
    } catch {
      // silent — non-critical
    }
  }, []);

  useEffect(() => {
    fetchMutes();
  }, [fetchMutes]);

  const mute = useCallback(async (target: string, targetType: string = 'Pipeline') => {
    try {
      await apiFetch('/api/notifications/mutes', {
        method: 'POST',
        body: JSON.stringify({ target, targetType }),
      });
      await fetchMutes();
    } catch {
      // silent
    }
  }, [fetchMutes]);

  const unmute = useCallback(async (muteId: number) => {
    try {
      await apiFetch(`/api/notifications/mutes/${muteId}`, { method: 'DELETE' });
      await fetchMutes();
    } catch {
      // silent
    }
  }, [fetchMutes]);

  const isMuted = useCallback((target: string) => {
    return mutes.some(m => m.target === target);
  }, [mutes]);

  const getMuteId = useCallback((target: string) => {
    return mutes.find(m => m.target === target)?.muteId;
  }, [mutes]);

  return { mutes, canMute, mute, unmute, isMuted, getMuteId, fetchMutes };
}
