import { useRef, useEffect, useState, useMemo } from 'react';
import { useVirtualizer } from '@tanstack/react-virtual';
import { useExecutionDashboard } from '../../hooks/useExecutionDashboard';

const severityClass: Record<string, string> = {
  Error: 'text-acc-red',
  Warning: 'text-acc-amber',
  Success: 'text-acc-green',
  Info: 'text-text-secondary',
};

const sessionColors = [
  'bg-accent/10',
  'bg-acc-green/10',
  'bg-acc-amber/10',
  'bg-acc-red/10',
  'bg-purple-500/10',
  'bg-teal-500/10',
  'bg-orange-500/10',
  'bg-pink-500/10',
];

export function LogPanel() {
  const { filteredLogs, state, selectSession, selectAgent } = useExecutionDashboard();
  const parentRef = useRef<HTMLDivElement>(null);
  const [autoScroll, setAutoScroll] = useState(true);
  const [searchText, setSearchText] = useState('');
  const [severityFilter, setSeverityFilter] = useState('All');

  // Session-to-color mapping
  const sessionColorMap = useMemo(() => {
    const map = new Map<string, string>();
    let idx = 0;
    for (const s of state.sessions.values()) {
      map.set(s.sessionId, sessionColors[idx % sessionColors.length]);
      idx++;
    }
    return map;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [state.sessions.size]);

  // Apply local filters
  const displayLogs = useMemo(() => {
    let logs = filteredLogs;
    if (severityFilter !== 'All') {
      logs = logs.filter(l => l.severity === severityFilter);
    }
    if (searchText) {
      const lower = searchText.toLowerCase();
      logs = logs.filter(l =>
        l.message.toLowerCase().includes(lower) ||
        l.agentName.toLowerCase().includes(lower));
    }
    return logs;
  }, [filteredLogs, severityFilter, searchText]);

  const virtualizer = useVirtualizer({
    count: displayLogs.length,
    getScrollElement: () => parentRef.current,
    estimateSize: () => 24,
    overscan: 30,
  });

  // Auto-scroll
  useEffect(() => {
    if (autoScroll && displayLogs.length > 0) {
      virtualizer.scrollToIndex(displayLogs.length - 1);
    }
  }, [displayLogs.length, autoScroll, virtualizer]);

  const errorCount = filteredLogs.filter(l => l.severity === 'Error').length;

  return (
    <div className="flex flex-col h-full overflow-hidden">
      {/* Log toolbar */}
      <div className="flex items-center gap-2 px-3 py-1.5 border-b border-bdr text-xs shrink-0">
        <span className="font-medium text-text-secondary">Log</span>
        <span className="text-[11px] text-text-muted">
          {displayLogs.length.toLocaleString()} entries
        </span>
        {errorCount > 0 && (
          <span className="text-[10px] font-medium px-1.5 py-0.5 rounded bg-acc-red/10 text-acc-red">
            {errorCount} errors
          </span>
        )}

        <div className="flex-1" />

        <input
          type="text"
          placeholder="Search..."
          value={searchText}
          onChange={e => setSearchText(e.target.value)}
          className="w-36 text-[11px] bg-bg-surface text-text-primary border border-bdr rounded px-1.5 py-0.5 focus:outline-none focus:border-accent"
        />

        <select
          value={severityFilter}
          onChange={e => setSeverityFilter(e.target.value)}
          className="text-[11px] bg-bg-surface text-text-primary border border-bdr rounded px-1 py-0.5"
        >
          <option value="All">All levels</option>
          <option value="Error">Errors only</option>
          <option value="Warning">Warnings</option>
          <option value="Info">Info</option>
        </select>

        <label className="flex items-center gap-1 text-[11px] text-text-muted">
          <input
            type="checkbox"
            checked={autoScroll}
            onChange={e => setAutoScroll(e.target.checked)}
          />
          Auto-scroll
        </label>

        {state.selectedSessionId && (
          <button
            onClick={() => selectSession(null)}
            className="text-[10px] px-2 py-0.5 text-text-muted hover:text-text-secondary border border-bdr rounded"
          >
            Clear filter
          </button>
        )}
      </div>

      {/* Virtualized log rows */}
      <div ref={parentRef} className="flex-1 overflow-auto">
        <div
          style={{
            height: `${virtualizer.getTotalSize()}px`,
            position: 'relative',
          }}
        >
          {virtualizer.getVirtualItems().map(row => {
            const entry = displayLogs[row.index];
            return (
              <div
                key={row.index}
                className="absolute top-0 left-0 w-full flex items-center px-3 gap-2 font-mono border-b border-bdr/50"
                style={{
                  height: `${row.size}px`,
                  transform: `translateY(${row.start}px)`,
                  fontSize: '11px',
                }}
              >
                {/* Timestamp */}
                <span className="w-14 shrink-0 text-text-muted text-[10px]">
                  {entry.timestamp}
                </span>

                {/* Session badge */}
                {entry.sessionId && (
                  <span
                    onClick={() => selectSession(entry.sessionId)}
                    className={`text-[9px] font-medium px-1.5 py-0.5 rounded cursor-pointer shrink-0 text-text-secondary ${
                      sessionColorMap.get(entry.sessionId) || 'bg-bg-surface'
                    }`}
                  >
                    {state.sessions.get(entry.sessionId)?.watchItemTag ||
                      entry.sessionId.substring(0, 6)}
                  </span>
                )}

                {/* Agent name */}
                {entry.agentName && (
                  <span
                    onClick={() => selectAgent(entry.agentName)}
                    className="w-14 shrink-0 text-accent cursor-pointer truncate"
                  >
                    {entry.agentName}
                  </span>
                )}

                {/* Message */}
                <span className={`flex-1 overflow-hidden text-ellipsis whitespace-nowrap ${
                  severityClass[entry.severity] || 'text-text-secondary'
                }`}>
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
