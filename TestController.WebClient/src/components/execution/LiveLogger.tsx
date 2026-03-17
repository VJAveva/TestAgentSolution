import { useRef, useEffect } from 'react';
import { Pause, Play, Trash2 } from 'lucide-react';
import { useExecutionStore } from '../../stores/executionStore';

export default function LiveLogger() {
  const logs = useExecutionStore(s => s.logs);
  const isPaused = useExecutionStore(s => s.isLogPaused);
  const togglePause = useExecutionStore(s => s.togglePause);
  const clearLogs = useExecutionStore(s => s.clearLogs);
  const endRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!isPaused) {
      endRef.current?.scrollIntoView({ behavior: 'smooth' });
    }
  }, [logs.length, isPaused]);

  return (
    <div className="flex flex-col h-full">
      <div className="flex items-center gap-2 mb-2">
        <h2 className="text-sm font-semibold text-text-primary flex-1">Live Execution Log</h2>
        <span className="text-xs text-text-muted">{logs.length} entries</span>
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

      <div className="flex-1 overflow-auto bg-bg-panel rounded-lg border border-bdr p-2 font-mono text-xs leading-5">
        {logs.length === 0 && <p className="text-text-muted">No log entries yet. Trigger an execution to see live output.</p>}
        {logs.map((entry, i) => {
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
