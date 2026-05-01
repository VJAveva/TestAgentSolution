import { useState, useMemo, useRef, useEffect } from 'react';
import { Bug, X, Trash2, Download, ChevronDown, ChevronUp } from 'lucide-react';
import { useAppLog, useAppLogCounts, type AppLogEntry, type LogLevel } from '../../hooks/useAppLog';
import { appLogger } from '../../lib/logger';

const LEVEL_STYLES: Record<LogLevel, string> = {
  debug: 'text-text-muted',
  info: 'text-blue-400',
  warn: 'text-acc-yellow',
  error: 'text-acc-red font-semibold',
};

const LEVEL_BG: Record<LogLevel, string> = {
  debug: '',
  info: '',
  warn: 'bg-acc-yellow/5',
  error: 'bg-acc-red/5',
};

export default function AppLogPanel() {
  const [open, setOpen] = useState(false);
  const [levelFilter, setLevelFilter] = useState<LogLevel | 'all'>('all');
  const [categoryFilter, setCategoryFilter] = useState('');
  const [expanded, setExpanded] = useState<string | null>(null);
  const counts = useAppLogCounts();
  const entries = useAppLog();
  const bottomRef = useRef<HTMLDivElement>(null);

  const filtered = useMemo(() => {
    return entries.filter(e => {
      if (levelFilter !== 'all' && e.level !== levelFilter) return false;
      if (categoryFilter && e.category !== categoryFilter) return false;
      return true;
    });
  }, [entries, levelFilter, categoryFilter]);

  const categories = useMemo(() =>
    [...new Set(entries.map(e => e.category))].sort(), [entries]);

  // Auto scroll
  useEffect(() => {
    if (open) bottomRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [filtered.length, open]);

  const exportLog = () => {
    const text = filtered.map(e =>
      `[${e.timestamp}] [${e.level.toUpperCase()}] [${e.category}] ${e.message}${e.data ? '\n  ' + JSON.stringify(e.data) : ''}`
    ).join('\n');
    const blob = new Blob([text], { type: 'text/plain' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `app-log-${new Date().toISOString().slice(0, 16)}.txt`;
    a.click();
    URL.revokeObjectURL(url);
  };

  const errorCount = counts.error;
  const warnCount = counts.warn;

  return (
    <>
      {/* Toggle button — fixed bottom-right */}
      <button
        onClick={() => setOpen(v => !v)}
        className="fixed bottom-4 right-4 z-[9999] flex items-center gap-1.5 px-3 py-2 rounded-full
                   bg-bg-ribbon border border-bdr shadow-lg text-xs font-medium
                   hover:bg-white/10 transition-colors"
      >
        <Bug size={14} className={errorCount > 0 ? 'text-acc-red' : 'text-text-muted'} />
        <span className="text-text-primary">Log</span>
        {errorCount > 0 && (
          <span className="px-1.5 py-0.5 bg-acc-red/20 text-acc-red text-[10px] font-bold rounded-full">
            {errorCount}
          </span>
        )}
        {warnCount > 0 && (
          <span className="px-1.5 py-0.5 bg-acc-yellow/20 text-acc-yellow text-[10px] font-bold rounded-full">
            {warnCount}
          </span>
        )}
      </button>

      {/* Panel */}
      {open && (
        <div className="fixed bottom-14 right-4 z-[9998] w-[600px] max-h-[70vh] flex flex-col
                        bg-bg-panel border border-bdr rounded-lg shadow-2xl overflow-hidden">
          {/* Header */}
          <div className="flex items-center justify-between px-3 py-2 border-b border-bdr bg-bg-ribbon">
            <span className="text-xs font-bold text-text-primary">Application Log</span>
            <div className="flex items-center gap-1">
              <button onClick={() => appLogger.clear()} title="Clear"
                className="p-1 rounded hover:bg-white/10 text-text-muted hover:text-acc-red">
                <Trash2 size={12} />
              </button>
              <button onClick={exportLog} title="Export"
                className="p-1 rounded hover:bg-white/10 text-text-muted hover:text-text-primary">
                <Download size={12} />
              </button>
              <button onClick={() => setOpen(false)} title="Close"
                className="p-1 rounded hover:bg-white/10 text-text-muted hover:text-text-primary">
                <X size={12} />
              </button>
            </div>
          </div>

          {/* Filters */}
          <div className="flex items-center gap-2 px-3 py-1.5 border-b border-bdr/50 bg-bg-card/50">
            <select value={levelFilter} onChange={e => setLevelFilter(e.target.value as any)}
              className="bg-bg-panel border border-bdr rounded px-2 py-0.5 text-[10px] text-text-secondary">
              <option value="all">All Levels</option>
              <option value="error">Error ({counts.error})</option>
              <option value="warn">Warn ({counts.warn})</option>
              <option value="info">Info ({counts.info})</option>
              <option value="debug">Debug ({counts.debug})</option>
            </select>
            <select value={categoryFilter} onChange={e => setCategoryFilter(e.target.value)}
              className="bg-bg-panel border border-bdr rounded px-2 py-0.5 text-[10px] text-text-secondary">
              <option value="">All Categories</option>
              {categories.map(c => <option key={c} value={c}>{c}</option>)}
            </select>
            <span className="ml-auto text-[10px] text-text-muted">{filtered.length} entries</span>
          </div>

          {/* Log entries */}
          <div className="flex-1 overflow-auto font-mono text-[11px] leading-relaxed">
            {filtered.length === 0 && (
              <p className="p-4 text-text-muted text-center">No log entries yet.</p>
            )}
            {filtered.map(entry => (
              <LogRow key={entry.id} entry={entry} expanded={expanded === entry.id}
                onToggle={() => setExpanded(expanded === entry.id ? null : entry.id)} />
            ))}
            <div ref={bottomRef} />
          </div>
        </div>
      )}
    </>
  );
}

function LogRow({ entry, expanded, onToggle }: { entry: AppLogEntry; expanded: boolean; onToggle: () => void }) {
  const hasData = entry.data !== undefined;
  return (
    <div className={`px-3 py-1 border-b border-bdr/20 hover:bg-white/5 ${LEVEL_BG[entry.level]}`}>
      <div className="flex items-start gap-2 cursor-pointer" onClick={hasData ? onToggle : undefined}>
        <span className="text-text-muted w-16 flex-shrink-0">
          {new Date(entry.timestamp).toLocaleTimeString('en-GB', { hour12: false })}
        </span>
        <span className={`w-10 flex-shrink-0 uppercase text-[9px] font-bold ${LEVEL_STYLES[entry.level]}`}>
          {entry.level}
        </span>
        <span className="text-accent/70 w-16 flex-shrink-0 truncate">{entry.category}</span>
        <span className={`flex-1 break-words ${LEVEL_STYLES[entry.level]}`}>{entry.message}</span>
        {entry.correlationId && (
          <span className="text-[9px] text-text-muted flex-shrink-0">[{entry.correlationId}]</span>
        )}
        {hasData && (
          <span className="flex-shrink-0 text-text-muted">
            {expanded ? <ChevronUp size={10} /> : <ChevronDown size={10} />}
          </span>
        )}
      </div>
      {expanded && hasData && (
        <pre className="mt-1 ml-28 p-2 bg-bg-surface rounded text-[10px] text-text-secondary overflow-auto max-h-40 whitespace-pre-wrap">
          {typeof entry.data === 'string' ? entry.data : JSON.stringify(entry.data, null, 2)}
        </pre>
      )}
    </div>
  );
}
