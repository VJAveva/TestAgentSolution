import { useRef, useEffect, useState, useMemo, useCallback } from 'react';
import { useRenderCount } from '../../hooks/useRenderCount';
import { useVirtualizer } from '@tanstack/react-virtual';
import { useExecutionDashboard } from '../../hooks/useExecutionDashboard';
import { StatsBar } from './StatsBar';

const severityRowClass: Record<string, string> = {
  Error:   'text-acc-red',
  Warning: 'text-acc-amber',
  Success: 'text-acc-green',
  Info:    'text-text-secondary',
};

export default function UnifiedLogView() {
  useRenderCount('UnifiedLogView');
  const { state, filteredLogs, selectSession, selectAgent } = useExecutionDashboard();
  const parentRef = useRef<HTMLDivElement>(null);
  const [autoScroll, setAutoScroll] = useState(true);
  const [searchText, setSearchText] = useState('');
  const [severityFilter, setSeverityFilter] = useState('All');
  const [selectedSessionFilter, setSelectedSessionFilter] = useState<string | null>(null);
  const [agentToggles, setAgentToggles] = useState<Record<string, boolean>>({});

  // Derive unique sessions and agents
  const sessions = useMemo(
    () => Array.from(state.sessions.values()),
    [state.sessions]
  );

  const agents = useMemo(() => {
    const set = new Set<string>();
    for (const entry of state.logs) {
      if (entry.agentName) set.add(entry.agentName);
    }
    return Array.from(set).sort();
  }, [state.logs]);

  // Initialize agent toggles (all on)
  useEffect(() => {
    setAgentToggles(prev => {
      const next = { ...prev };
      for (const a of agents) {
        if (!(a in next)) next[a] = true;
      }
      return next;
    });
  }, [agents]);

  const toggleAgent = useCallback((name: string) => {
    setAgentToggles(prev => ({ ...prev, [name]: !prev[name] }));
  }, []);

  // Apply filters
  const displayLogs = useMemo(() => {
    let logs = filteredLogs;

    if (selectedSessionFilter) {
      logs = logs.filter(l => l.sessionId === selectedSessionFilter);
    }
    if (severityFilter !== 'All') {
      logs = logs.filter(l => l.severity === severityFilter);
    }
    if (searchText) {
      const lower = searchText.toLowerCase();
      logs = logs.filter(l =>
        l.message.toLowerCase().includes(lower) ||
        l.agentName.toLowerCase().includes(lower));
    }
    // Agent toggles
    const activeAgents = Object.entries(agentToggles)
      .filter(([, on]) => on).map(([n]) => n);
    if (activeAgents.length > 0 && activeAgents.length < agents.length) {
      const set = new Set(activeAgents);
      logs = logs.filter(l => !l.agentName || set.has(l.agentName));
    }

    return logs;
  }, [filteredLogs, selectedSessionFilter, severityFilter, searchText, agentToggles, agents]);

  const virtualizer = useVirtualizer({
    count: displayLogs.length,
    getScrollElement: () => parentRef.current,
    estimateSize: () => 24,
    overscan: 40,
  });

  // Auto-scroll
  useEffect(() => {
    if (autoScroll && displayLogs.length > 0) {
      virtualizer.scrollToIndex(displayLogs.length - 1);
    }
  }, [displayLogs.length, autoScroll, virtualizer]);

  const errorCount = displayLogs.filter(l => l.severity === 'Error').length;

  return (
    <div className="flex flex-col h-full overflow-hidden">
      <StatsBar />

      {/* Filter toolbar */}
      <div className="flex flex-wrap items-center gap-2 px-4 py-2 border-b border-bdr text-xs">
        {/* Session dropdown */}
        <label className="text-text-muted">Session:</label>
        <select
          value={selectedSessionFilter || ''}
          onChange={e => {
            const v = e.target.value || null;
            setSelectedSessionFilter(v);
            selectSession(v);
          }}
          className="text-[11px] bg-bg-surface text-text-primary border border-bdr rounded px-1.5 py-0.5"
        >
          <option value="">All Sessions</option>
          {sessions.map(s => (
            <option key={s.sessionId} value={s.sessionId}>
              {s.watchItemTag}
            </option>
          ))}
        </select>

        {/* Severity dropdown */}
        <label className="text-text-muted ml-2">Level:</label>
        <select
          value={severityFilter}
          onChange={e => setSeverityFilter(e.target.value)}
          className="text-[11px] bg-bg-surface text-text-primary border border-bdr rounded px-1.5 py-0.5"
        >
          <option>All</option>
          <option>Error</option>
          <option>Warning</option>
          <option>Success</option>
          <option>Info</option>
        </select>

        {/* Agent toggle buttons */}
        <label className="text-text-muted ml-2">Agents:</label>
        <div className="flex gap-1 flex-wrap">
          {agents.map(a => (
            <button
              key={a}
              onClick={() => toggleAgent(a)}
              className={`text-[10px] px-2 py-0.5 rounded border transition-colors ${
                agentToggles[a]
                  ? 'bg-accent/10 text-accent border-accent/30'
                  : 'bg-transparent text-text-muted border-bdr'
              }`}
            >
              {a}
            </button>
          ))}
        </div>

        <div className="flex-1" />

        {/* Search */}
        <input
          type="text"
          placeholder="Search..."
          value={searchText}
          onChange={e => setSearchText(e.target.value)}
          className="w-36 text-[11px] bg-bg-surface text-text-primary border border-bdr rounded px-1.5 py-0.5 focus:outline-none focus:border-accent"
        />

        {errorCount > 0 && (
          <span className="text-[10px] px-1.5 py-0.5 rounded bg-acc-red/10 text-acc-red font-medium">
            {errorCount} errors
          </span>
        )}

        <span className="text-text-muted">
          {displayLogs.length.toLocaleString()} entries
        </span>

        <button
          onClick={() => setAutoScroll(!autoScroll)}
          className={`text-[10px] px-2 py-0.5 rounded border ${
            autoScroll
              ? 'bg-accent/10 text-accent border-accent/30'
              : 'text-text-muted border-bdr'
          }`}
        >
          Auto-scroll
        </button>
      </div>

      {/* Virtualized log table */}
      <div ref={parentRef} className="flex-1 overflow-auto">
        <div
          style={{ height: virtualizer.getTotalSize(), position: 'relative', width: '100%' }}
        >
          {virtualizer.getVirtualItems().map(virtualRow => {
            const entry = displayLogs[virtualRow.index];
            const rowColor = severityRowClass[entry.severity] || severityRowClass.Info;

            return (
              <div
                key={virtualRow.index}
                className={`absolute left-0 right-0 flex items-center px-3 text-[11px] font-mono ${rowColor} hover:bg-white/[0.02]`}
                style={{
                  height: virtualRow.size,
                  transform: `translateY(${virtualRow.start}px)`,
                }}
              >
                <span className="w-[60px] shrink-0 text-text-muted">{entry.timestamp}</span>
                <span
                  className="w-[90px] shrink-0 text-[9px] font-medium text-text-secondary bg-bg-surface rounded px-1 mr-1 cursor-pointer truncate"
                  onClick={() => selectSession(entry.sessionId)}
                >
                  {sessions.find(s => s.sessionId === entry.sessionId)?.watchItemTag || entry.sessionId}
                </span>
                <span
                  className="w-[80px] shrink-0 text-cyan-400 cursor-pointer"
                  onClick={() => selectAgent(entry.agentName)}
                >
                  {entry.agentName}
                </span>
                <span className="flex-1 truncate">{entry.message}</span>
              </div>
            );
          })}
        </div>
      </div>
    </div>
  );
}
