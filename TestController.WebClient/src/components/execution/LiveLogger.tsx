import { useRef, useMemo, useEffect } from 'react';
import { Pause, Play, Trash2 } from 'lucide-react';
import { useExecutionStore } from '../../stores/executionStore';

export default function LiveLogger() {
  const logs = useExecutionStore(s => s.logs);
  const isPaused = useExecutionStore(s => s.isLogPaused);
  const togglePause = useExecutionStore(s => s.togglePause);
  const clearLogs = useExecutionStore(s => s.clearLogs);
  const sessionFilter = useExecutionStore(s => s.logSessionFilter);
  const setSessionFilter = useExecutionStore(s => s.setLogSessionFilter);
  const endRef = useRef<HTMLDivElement>(null);

  // Derive unique session IDs for the filter bar
  const sessionIds = useMemo(() => {
    const ids = new Set<string>();
    for (const entry of logs) {
      if (entry.sessionId) ids.add(entry.sessionId);
    }
    return Array.from(ids);
  }, [logs]);

  const filteredLogs = useMemo(() => {
    if (!sessionFilter) return logs;
    return logs.filter(e => e.sessionId === sessionFilter || !e.sessionId);
  }, [logs, sessionFilter]);

  useEffect(() => {
    if (!isPaused) {
      endRef.current?.scrollIntoView({ behavior: 'smooth' });
    }
  }, [filteredLogs.length, isPaused]);

  return (
    <div className="flex flex-col h-full">
      <div className="flex items-center gap-2 mb-2">
        <h2 className="text-sm font-semibold text-text-primary flex-1">Live Execution Log</h2>
        <span className="text-xs text-text-muted">{filteredLogs.length} entries</span>
        <button
          className={`p-1.5 rounded text-xs ${isPaused ? 'bg-acc-yellow/20 text-acc-yellow' : 'bg-white/5 text-text-muted hover:text-text-primary'}`}
          onClick={togglePause} title={isPaused ? 'Resume' : 'Pause'}
        >
          {isPaused ? <Play size={12} /> : <Pause size={12} />}
        </button>
        <button className="p-1.5 rounded bg-white/5 text-text-muted hover:text-acc-red" onClick={clearLogs} title="Clear">
          <Trash2 size={12} />
        </button>
      </div>

      {/* Session filter chips */}
      {sessionIds.length > 1 && (
        <div className="flex flex-wrap gap-1 mb-2">
          <button
            className={`px-2 py-0.5 rounded text-[10px] font-medium transition-colors ${
              !sessionFilter ? 'bg-accent/20 text-accent' : 'bg-white/5 text-text-muted hover:bg-white/10'
            }`}
            onClick={() => setSessionFilter('')}
          >
            All
          </button>
          {sessionIds.map(id => (
            <button
              key={id}
              className={`px-2 py-0.5 rounded text-[10px] font-mono transition-colors ${
                sessionFilter === id ? 'bg-accent/20 text-accent' : 'bg-white/5 text-text-muted hover:bg-white/10'
              }`}
              onClick={() => setSessionFilter(sessionFilter === id ? '' : id)}
            >
              [{id.slice(0, 6)}]
            </button>
          ))}
        </div>
      )}

      <div className="flex-1 overflow-auto bg-bg-panel rounded-lg border border-bdr p-2 font-mono text-xs leading-5">
        {filteredLogs.length === 0 && <p className="text-text-muted">No log entries yet. Trigger an execution to see live output.</p>}
        {filteredLogs.map((entry, i) => {
          const color =
            entry.severity === 'error' || entry.kind === 'stderr' ? 'text-acc-red'
            : entry.severity === 'success' ? 'text-acc-green'
            : entry.severity === 'warning' ? 'text-acc-yellow'
            : 'text-text-primary';
          return (
            <div key={i} className={`${color} whitespace-pre-wrap break-all`}>
              <span className="text-text-muted mr-2">
                {new Date(entry.timestamp).toLocaleTimeString('en-GB', { hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit' })}
              </span>
              {entry.sessionId && (
                <span className="text-accent/60 mr-1">[{entry.sessionId.slice(0, 6)}]</span>
              )}
              {entry.agent && <span className="text-acc-mauve mr-1">[{entry.agent}]</span>}
              {entry.message}
            </div>
          );
        })}
        <div ref={endRef} />
      </div>
    </div>
  );
}
