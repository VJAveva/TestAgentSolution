import { useState, useCallback } from 'react';
import { useExecutionDashboard } from '../../hooks/useExecutionDashboard';
import { SessionCard } from './SessionCard';
import { LogPanel } from './LogPanel';
import { StatsBar } from './StatsBar';
import { apiFetch } from '../../lib/api';
import { logCatch } from '../../lib/logger';

export default function ExecutionDashboard() {
  const {
    activeSessions, completedSessions,
    state, dispatch, selectSession
  } = useExecutionDashboard();

  const [splitPercent, setSplitPercent] = useState(60);
  const [showCompleted, setShowCompleted] = useState(false);
  const [filterText, setFilterText] = useState('');

  const loadDemoData = useCallback(() => {
    apiFetch<{ active: any[]; history: any[] }>('/api/execution/demo-sessions')
      .then(data => {
        const all = [
          ...(data.active || []),
          ...(data.history || []),
        ];
        dispatch({ type: 'SET_SESSIONS', sessions: all });
        setShowCompleted(true);
      })
      .catch(logCatch('ExecutionDashboard', 'loadDemoData'));
  }, [dispatch]);

  const filteredActive = activeSessions.filter(s =>
    !filterText ||
    s.watchItemTag.toLowerCase().includes(filterText.toLowerCase()) ||
    s.userId.toLowerCase().includes(filterText.toLowerCase())
  );

  return (
    <div className="flex flex-col h-full overflow-hidden">
      {/* Stats bar */}
      <StatsBar />

      {/* Toolbar */}
      <div className="flex items-center gap-2 px-4 py-2 border-b border-bdr">
        <input
          type="text"
          placeholder="Filter sessions or agents..."
          value={filterText}
          onChange={e => setFilterText(e.target.value)}
          className="flex-1 bg-bg-surface text-text-primary text-xs border border-bdr rounded px-2 py-1 focus:outline-none focus:border-accent"
        />
        <button
          onClick={loadDemoData}
          className="text-xs px-3 py-1 rounded border border-accent/30 text-accent bg-accent/10 hover:bg-accent/20 transition-colors"
          title="Load demo data to test the dashboard UI"
        >
          &#9654; Demo
        </button>
        <button
          onClick={() => setShowCompleted(!showCompleted)}
          className={`text-xs px-3 py-1 rounded border transition-colors ${
            showCompleted
              ? 'bg-accent/10 text-accent border-accent/30'
              : 'bg-transparent text-text-muted border-bdr hover:text-text-secondary'
          }`}
        >
          Show completed
        </button>
      </div>

      {/* Split panels */}
      <div className="flex flex-col flex-1 overflow-hidden">

        {/* Panel 1: Session cards */}
        <div
          className="overflow-auto px-4 py-3"
          style={{ height: `${splitPercent}%` }}
        >
          {filteredActive.length === 0 && (
            <div className="text-center py-12 text-text-muted text-sm">
              No active sessions. Trigger a pipeline to see execution here.
            </div>
          )}

          {filteredActive.map(session => (
            <SessionCard
              key={session.sessionId}
              session={session}
              isSelected={state.selectedSessionId === session.sessionId}
              onSelect={() =>
                selectSession(
                  state.selectedSessionId === session.sessionId
                    ? null : session.sessionId)}
            />
          ))}

          {showCompleted && completedSessions.length > 0 && (
            <>
              <div className="text-[11px] font-medium text-text-muted uppercase tracking-widest mt-4 mb-2">
                Completed sessions
              </div>
              {completedSessions.map(session => (
                <SessionCard
                  key={session.sessionId}
                  session={session}
                  isSelected={state.selectedSessionId === session.sessionId}
                  onSelect={() => selectSession(session.sessionId)}
                  collapsed
                />
              ))}
            </>
          )}
        </div>

        {/* Splitter */}
        <div
          className="h-1.5 cursor-row-resize bg-bg-surface border-y border-bdr flex items-center justify-center shrink-0"
          onMouseDown={(e) => {
            const startY = e.clientY;
            const startPercent = splitPercent;
            const container = e.currentTarget.parentElement;
            if (!container) return;
            const totalH = container.clientHeight;

            const onMove = (me: MouseEvent) => {
              const delta = me.clientY - startY;
              const newPercent = startPercent + (delta / totalH * 100);
              setSplitPercent(Math.max(20, Math.min(80, newPercent)));
            };
            const onUp = () => {
              document.removeEventListener('mousemove', onMove);
              document.removeEventListener('mouseup', onUp);
            };
            document.addEventListener('mousemove', onMove);
            document.addEventListener('mouseup', onUp);
          }}
        >
          <div className="w-10 h-0.5 rounded bg-bdr" />
        </div>

        {/* Panel 2: Log */}
        <div
          className="overflow-hidden"
          style={{ height: `${100 - splitPercent}%` }}
        >
          <LogPanel />
        </div>
      </div>
    </div>
  );
}
