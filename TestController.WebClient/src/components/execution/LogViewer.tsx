import { useState, useEffect, useRef, useMemo, useCallback } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import { useConnectionStore } from '../../stores/connectionStore';
import { Download, Pause, Play, Trash2 } from 'lucide-react';

interface LogEntry {
  timestamp: string;
  sessionId: string;
  severity: string;
  agent: string;
  message: string;
}

const MAX_LOG_ENTRIES = 50000;

const SEVERITY_COLORS: Record<string, string> = {
  error: 'text-acc-red font-semibold',
  warning: 'text-acc-yellow',
  success: 'text-acc-green',
  info: 'text-text-primary',
};

export default function LogViewer() {
  const [allEntries, setAllEntries] = useState<LogEntry[]>([]);
  const [filterSession, setFilterSession] = useState('');
  const [filterAgent, setFilterAgent] = useState('');
  const [filterSeverity, setFilterSeverity] = useState('All');
  const [searchText, setSearchText] = useState('');
  const [isRegex, setIsRegex] = useState(false);
  const [errorsOnly, setErrorsOnly] = useState(false);
  const [autoScroll, setAutoScroll] = useState(true);
  const [paused, setPaused] = useState(false);
  const parentRef = useRef<HTMLDivElement>(null);
  const bufferRef = useRef<LogEntry[]>([]);
  const connection = useConnectionStore(s => s.connection);

  // Subscribe to SignalR log events directly for high-capacity buffering
  useEffect(() => {
    if (!connection) return;

    const handleLog = (entry: { message?: string; agent?: string; agentName?: string; sessionId?: string; timestamp?: string; severity?: string; category?: string }) => {
      const logEntry: LogEntry = {
        message: entry.message ?? '',
        agent: entry.agentName || entry.agent || entry.category || '',
        sessionId: entry.sessionId ?? '',
        timestamp: entry.timestamp ?? new Date().toISOString(),
        severity: (entry.severity?.toLowerCase()) ?? 'info',
      };

      if (paused) {
        bufferRef.current.push(logEntry);
        if (bufferRef.current.length > MAX_LOG_ENTRIES)
          bufferRef.current = bufferRef.current.slice(-MAX_LOG_ENTRIES);
        return;
      }

      setAllEntries(prev => {
        const next = [...prev, logEntry];
        return next.length > MAX_LOG_ENTRIES ? next.slice(-MAX_LOG_ENTRIES) : next;
      });
    };

    const handleAgentOutput = (data: { agentName?: string; line?: string; kind?: string; timestamp?: string }) => {
      handleLog({
        message: data.line ?? '',
        agentName: data.agentName,
        timestamp: data.timestamp ?? new Date().toISOString(),
        severity: data.kind === 'stderr' ? 'error' : 'info',
      });
    };

    const handleExecutionStarted = (data: { sessionId?: string; watchItemTag?: string; eventType?: string }) => {
      handleLog({
        message: `Execution started: ${data.watchItemTag} (${data.eventType})`,
        sessionId: data.sessionId,
        severity: 'info',
      });
    };

    const handleExecutionCompleted = (data: { sessionId?: string; watchItemTag?: string; state?: string }) => {
      handleLog({
        message: `Execution ${data.state}: ${data.watchItemTag}`,
        sessionId: data.sessionId,
        severity: data.state === 'Success' ? 'success' : data.state === 'Failed' ? 'error' : 'warning',
      });
    };

    connection.on('LogEntry', handleLog);
    connection.on('AgentOutput', handleAgentOutput);
    connection.on('ExecutionStarted', handleExecutionStarted);
    connection.on('ExecutionCompleted', handleExecutionCompleted);

    return () => {
      connection.off('LogEntry', handleLog);
      connection.off('AgentOutput', handleAgentOutput);
      connection.off('ExecutionStarted', handleExecutionStarted);
      connection.off('ExecutionCompleted', handleExecutionCompleted);
    };
  }, [connection, paused]);

  // Resume: flush buffer
  const resume = useCallback(() => {
    setPaused(false);
    if (bufferRef.current.length > 0) {
      setAllEntries(prev => {
        const merged = [...prev, ...bufferRef.current];
        bufferRef.current = [];
        return merged.length > MAX_LOG_ENTRIES ? merged.slice(-MAX_LOG_ENTRIES) : merged;
      });
    }
  }, []);

  // Filtered entries (memoized)
  const filteredEntries = useMemo(() => {
    let regex: RegExp | null = null;
    if (isRegex && searchText) {
      try { regex = new RegExp(searchText, 'i'); } catch { regex = null; }
    }

    return allEntries.filter(e => {
      if (errorsOnly && e.severity !== 'error') return false;
      if (filterSession && e.sessionId !== filterSession) return false;
      if (filterAgent && e.agent !== filterAgent) return false;
      if (filterSeverity !== 'All' && e.severity !== filterSeverity.toLowerCase()) return false;
      if (searchText) {
        if (regex) {
          if (!regex.test(e.message) && !regex.test(e.timestamp)) return false;
        } else {
          const lower = searchText.toLowerCase();
          if (!e.message.toLowerCase().includes(lower) &&
              !e.timestamp.includes(lower)) return false;
        }
      }
      return true;
    });
  }, [allEntries, filterSession, filterAgent, filterSeverity, searchText, isRegex, errorsOnly]);

  // Virtual scrolling
  const virtualizer = useVirtualizer({
    count: filteredEntries.length,
    getScrollElement: () => parentRef.current,
    estimateSize: () => 24,
    overscan: 20,
  });

  // Auto-scroll to bottom
  useEffect(() => {
    if (autoScroll && filteredEntries.length > 0) {
      virtualizer.scrollToIndex(filteredEntries.length - 1);
    }
  }, [filteredEntries.length, autoScroll, virtualizer]);

  // Unique sessions and agents for filter dropdowns
  const sessions = useMemo(() =>
    [...new Set(allEntries.map(e => e.sessionId).filter(Boolean))], [allEntries]);
  const agents = useMemo(() =>
    [...new Set(allEntries.map(e => e.agent).filter(Boolean))], [allEntries]);
  const errorCount = useMemo(() =>
    allEntries.filter(e => e.severity === 'error').length, [allEntries]);

  // Export to CSV
  const exportCsv = () => {
    const header = 'Timestamp,Session,Severity,Agent,Message\n';
    const rows = filteredEntries.map(e =>
      `"${e.timestamp}","${e.sessionId}","${e.severity}","${e.agent}","${e.message.replace(/"/g, '""')}"`
    ).join('\n');
    const blob = new Blob([header + rows], { type: 'text/csv' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `execution-log-${new Date().toISOString().slice(0, 10)}.csv`;
    a.click();
    URL.revokeObjectURL(url);
  };

  return (
    <div className="p-4 h-full flex flex-col">
      {/* Header */}
      <div className="flex items-center justify-between mb-3">
        <div className="flex items-center gap-2">
          <h2 className="text-sm font-bold text-text-primary">Execution Log</h2>
          <span className="text-[10px] text-text-muted font-mono">
            {filteredEntries.length.toLocaleString()} entries
          </span>
          {errorCount > 0 && (
            <span className="px-1.5 py-0.5 bg-acc-red/20 text-acc-red text-[10px] font-bold rounded-full">
              {errorCount} errors
            </span>
          )}
          {paused && (
            <span className="px-1.5 py-0.5 bg-acc-yellow/20 text-acc-yellow text-[10px] font-bold rounded-full">
              PAUSED ({bufferRef.current.length} buffered)
            </span>
          )}
        </div>
        <div className="flex gap-1">
          <button onClick={() => paused ? resume() : setPaused(true)}
            className="p-1.5 rounded bg-white/5 text-text-muted hover:text-text-primary" title={paused ? 'Resume' : 'Pause'}>
            {paused ? <Play size={12} /> : <Pause size={12} />}
          </button>
          <button onClick={() => { setAllEntries([]); bufferRef.current = []; }}
            className="p-1.5 rounded bg-white/5 text-text-muted hover:text-acc-red" title="Clear">
            <Trash2 size={12} />
          </button>
          <button onClick={exportCsv}
            className="p-1.5 rounded bg-white/5 text-text-muted hover:text-text-primary" title="Export CSV">
            <Download size={12} />
          </button>
        </div>
      </div>

      {/* Filter bar */}
      <div className="flex flex-wrap items-center gap-2 mb-3">
        <select value={filterSession} onChange={e => setFilterSession(e.target.value)}
          className="bg-bg-panel border border-bdr rounded px-2 py-1 text-text-secondary text-[10px]">
          <option value="">All Sessions</option>
          {sessions.map(s => <option key={s} value={s}>[{s.slice(0, 8)}]</option>)}
        </select>

        <select value={filterAgent} onChange={e => setFilterAgent(e.target.value)}
          className="bg-bg-panel border border-bdr rounded px-2 py-1 text-text-secondary text-[10px]">
          <option value="">All Agents</option>
          {agents.map(a => <option key={a} value={a}>{a}</option>)}
        </select>

        <select value={filterSeverity} onChange={e => setFilterSeverity(e.target.value)}
          className="bg-bg-panel border border-bdr rounded px-2 py-1 text-text-secondary text-[10px]">
          <option value="All">All Levels</option>
          <option value="Error">Error</option>
          <option value="Warning">Warning</option>
          <option value="Success">Success</option>
          <option value="Info">Info</option>
        </select>

        <div className="flex items-center gap-1">
          <input type="text" value={searchText}
            onChange={e => setSearchText(e.target.value)}
            placeholder="Search..."
            className="bg-bg-panel border border-bdr rounded px-2 py-1 text-text-secondary text-[10px] w-32 outline-none focus:border-accent" />
          <button onClick={() => setIsRegex(!isRegex)}
            className={`px-1.5 py-1 text-[10px] font-mono rounded border transition-colors
              ${isRegex ? 'border-accent text-accent bg-accent/10' : 'border-bdr text-text-muted'}`}>
            .*
          </button>
        </div>

        <button onClick={() => setErrorsOnly(!errorsOnly)}
          className={`px-2 py-1 text-[10px] rounded border transition-colors
            ${errorsOnly ? 'border-acc-red text-acc-red bg-acc-red/10' : 'border-bdr text-text-muted'}`}>
          Errors Only
        </button>

        <label className="flex items-center gap-1 text-[10px] text-text-muted ml-auto">
          <input type="checkbox" checked={autoScroll}
            onChange={e => setAutoScroll(e.target.checked)} className="rounded" />
          Auto-scroll
        </label>
      </div>

      {/* Virtualized log list */}
      <div ref={parentRef} className="flex-1 overflow-auto bg-bg-panel rounded-lg border border-bdr">
        <div style={{ height: `${virtualizer.getTotalSize()}px`, position: 'relative' }}>
          {virtualizer.getVirtualItems().map(virtualRow => {
            const entry = filteredEntries[virtualRow.index];
            return (
              <div key={virtualRow.index}
                className="absolute top-0 left-0 w-full flex items-center px-3 py-0.5
                           hover:bg-white/5 border-b border-bg-surface"
                style={{
                  height: `${virtualRow.size}px`,
                  transform: `translateY(${virtualRow.start}px)`,
                }}>
                {/* Timestamp */}
                <span className="text-[10px] text-text-muted font-mono w-20 flex-shrink-0">
                  {new Date(entry.timestamp).toLocaleTimeString('en-GB', { hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit' })}
                </span>
                {/* Session badge */}
                {entry.sessionId && (
                  <span className="text-[9px] font-mono font-bold text-accent/60
                                   bg-accent/10 px-1 rounded mr-2 flex-shrink-0">
                    {entry.sessionId.slice(0, 6)}
                  </span>
                )}
                {/* Agent badge */}
                {entry.agent && (
                  <span className="text-[9px] font-mono text-acc-mauve mr-2 flex-shrink-0">
                    [{entry.agent}]
                  </span>
                )}
                {/* Message */}
                <span className={`text-xs font-mono truncate ${SEVERITY_COLORS[entry.severity] || 'text-text-primary'}`}>
                  {entry.message}
                </span>
              </div>
            );
          })}
        </div>
      </div>
    </div>
  );
}
