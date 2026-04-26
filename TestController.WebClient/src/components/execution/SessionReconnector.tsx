import { useEffect, useState } from 'react';
import { apiFetch } from '../../lib/api';
import { useConnectionStore } from '../../stores/connectionStore';
import { useExecutionStore } from '../../stores/executionStore';
import { joinSession } from '../../hooks/useSignalR';

interface ActiveSession {
  sessionId: string;
  watchItemTag: string;
  elapsed: string;
  lockedAgents: string[];
  status: string;
}

/**
 * Checks on page load whether the user has running sessions
 * (e.g., browser was closed mid-execution) and offers to reconnect.
 */
export default function SessionReconnector() {
  const [activeSessions, setActiveSessions] = useState<ActiveSession[]>([]);
  const [dismissed, setDismissed] = useState(false);
  const connection = useConnectionStore(s => s.connection);

  useEffect(() => {
    apiFetch<{ activeSessions: ActiveSession[] }>('/api/execution/reconnect')
      .then(data => {
        if (data.activeSessions.length > 0) {
          setActiveSessions(data.activeSessions);
        }
      })
      .catch(() => {});
  }, []);

  if (activeSessions.length === 0 || dismissed) return null;

  const reconnect = async (session: ActiveSession) => {
    // Join SignalR group for this session
    joinSession(connection, session.sessionId);

    // Fetch missed logs
    try {
      const result = await apiFetch<{ logs: any[] }>(
        `/api/execution/${session.sessionId}/recent-logs?count=200`
      );
      // Backfill logs into the execution store
      for (const log of result.logs) {
        useExecutionStore.getState().addLog({
          message: log.message ?? '',
          sessionId: session.sessionId,
          timestamp: log.timestamp ?? new Date().toISOString(),
          severity: 'info',
        });
      }
    } catch {
      // best effort
    }

    setActiveSessions(prev =>
      prev.filter(s => s.sessionId !== session.sessionId)
    );
  };

  return (
    <div className="fixed top-0 left-0 right-0 z-50 bg-amber-900/95 border-b border-amber-700 p-3">
      <div className="max-w-4xl mx-auto flex items-center justify-between">
        <div className="flex items-center gap-3">
          <span className="w-2 h-2 bg-amber-400 rounded-full animate-pulse" />
          <span className="text-amber-100 text-sm font-medium">
            You have {activeSessions.length} running session{activeSessions.length > 1 ? 's' : ''}
          </span>
        </div>
        <div className="flex gap-2">
          {activeSessions.map(s => (
            <button
              key={s.sessionId}
              onClick={() => reconnect(s)}
              className="px-3 py-1 bg-amber-700 hover:bg-amber-600 text-white text-xs rounded font-medium"
            >
              Reconnect: {s.watchItemTag} ({s.elapsed})
            </button>
          ))}
          <button
            onClick={() => setDismissed(true)}
            className="px-2 py-1 text-amber-400 hover:text-amber-200 text-xs"
          >
            Dismiss
          </button>
        </div>
      </div>
    </div>
  );
}
