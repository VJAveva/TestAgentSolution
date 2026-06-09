import { useEffect } from 'react';
import { useExecutionStore } from '../../stores/executionStore';
import { useExecution } from '../../hooks/useExecution';
import { logCatch } from '../../lib/logger';
import { PlayCircle, XCircle, X, RefreshCw } from 'lucide-react';

export default function SessionList() {
  const isExecuting = useExecutionStore(s => s.isExecuting);
  const activeCount = useExecutionStore(s => s.activeCount);
  const sessions = useExecutionStore(s => s.sessions);
  const { triggerAll, cancelAll, cancelSession, fetchSessions } = useExecution();

  // Poll sessions every 2s while executing
  useEffect(() => {
    if (!isExecuting) return;
    const id = setInterval(() => fetchSessions().catch(logCatch('SessionList', 'fetchSessions')), 2000);
    return () => clearInterval(id);
  }, [isExecuting, fetchSessions]);

  return (
    <div className="p-3 space-y-3">
      {/* Controls */}
      <div className="bg-bg-card rounded-lg p-3 space-y-2">
        <div className="flex items-center gap-2">
          <span className={`w-2 h-2 rounded-full ${isExecuting ? 'bg-accent animate-pulse' : 'bg-text-muted'}`} />
          <span className="text-xs text-text-primary font-medium">
            {isExecuting ? `${activeCount} session(s) active` : 'Idle'}
          </span>
          <button
            className="ml-auto p-1 rounded text-text-muted hover:text-text-primary"
            onClick={() => fetchSessions()}
            title="Refresh sessions"
          >
            <RefreshCw size={11} />
          </button>
        </div>

        <div className="flex gap-1">
          <button
            className="flex items-center gap-1 px-2 py-1 rounded text-xs bg-accent/15 text-accent hover:bg-accent/25"
            onClick={() => triggerAll()}
          >
            <PlayCircle size={12} /> Trigger All
          </button>
          <button
            className="flex items-center gap-1 px-2 py-1 rounded text-xs bg-acc-red/15 text-acc-red hover:bg-acc-red/25"
            onClick={() => cancelAll()}
          >
            <XCircle size={12} /> Cancel All
          </button>
        </div>
      </div>

      {/* Per-session rows */}
      {sessions.length > 0 && (
        <div className="space-y-1.5">
          {sessions.map(s => {
            const stateColor =
              s.state === 'Running' ? 'text-accent'
              : s.state === 'Completed' ? 'text-acc-green'
              : s.state === 'Failed' || s.state === 'PartialFailure' ? 'text-acc-red'
              : 'text-text-muted';
            const barColor =
              s.state === 'Running' ? 'bg-accent'
              : s.state === 'Completed' ? 'bg-acc-green'
              : 'bg-acc-red';

            return (
              <div key={s.sessionId} className="bg-bg-card rounded-lg p-2.5 border border-bdr">
                {/* Header: tag + state + cancel */}
                <div className="flex items-center gap-2 mb-1.5">
                  <span className="text-xs font-semibold text-text-primary truncate flex-1">
                    {s.watchItemTag}
                  </span>
                  <span className={`text-[10px] font-bold ${stateColor}`}>{s.state}</span>
                  {s.state === 'Running' && (
                    <button
                      className="p-0.5 rounded text-acc-red/60 hover:text-acc-red"
                      onClick={() => cancelSession(s.sessionId)}
                      title="Cancel session"
                    >
                      <X size={11} />
                    </button>
                  )}
                </div>

                {/* Progress bar */}
                <div className="h-1 rounded-full bg-bg-surface mb-1.5 overflow-hidden">
                  <div
                    className={`h-full rounded-full transition-all ${barColor}`}
                    style={{ width: `${Math.min(100, s.progressPercent ?? 0)}%` }}
                  />
                </div>

                {/* Stats */}
                <div className="flex items-center gap-2 text-[10px] text-text-muted font-mono">
                  <span>{(s.progressPercent ?? 0).toFixed(0)}%</span>
                  <span>{s.completedActions ?? 0}/{s.totalActions ?? 0}</span>
                  <span className="text-acc-green">{s.passedActions ?? 0}P</span>
                  {s.failedActions > 0 && <span className="text-acc-red">{s.failedActions}F</span>}
                  <span className="ml-auto text-[9px]">[{s.sessionId.slice(0, 6)}]</span>
                </div>
              </div>
            );
          })}
        </div>
      )}

      {sessions.length === 0 && !isExecuting && (
        <div className="text-xs text-text-muted">
          No active sessions. Use Trigger All or trigger individual WatchItems.
        </div>
      )}
    </div>
  );
}
