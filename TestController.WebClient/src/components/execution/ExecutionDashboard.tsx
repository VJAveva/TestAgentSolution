import { useState, useCallback, useEffect } from 'react';
import { useRenderCount } from '../../hooks/useRenderCount';
import { useExecutionDashboard } from '../../hooks/useExecutionDashboard';
import { SessionCard } from './SessionCard';
import { LogPanel } from './LogPanel';
import { StatsBar } from './StatsBar';
import { apiFetch } from '../../lib/api';
import { logCatch } from '../../lib/logger';

export default function ExecutionDashboard() {
  useRenderCount('ExecutionDashboard');
  const {
    activeSessions, completedSessions,
    state, dispatch, selectSession, reload
  } = useExecutionDashboard();

  const [splitPercent, setSplitPercent] = useState(60);
  const [showCompleted, setShowCompleted] = useState(false);
  const [filterText, setFilterText] = useState('');

  // Auto-show completed sessions when no active sessions exist
  useEffect(() => {
    if (activeSessions.length === 0 && completedSessions.length > 0) {
      setShowCompleted(true);
    }
  }, [activeSessions.length, completedSessions.length]);

  // Demo is a toggle. ON fetches sample sessions and adds them alongside whatever is already
  // on screen; OFF removes only those. Real runs are never replaced.
  const toggleDemo = useCallback(() => {
    if (state.demoOn) {
      dispatch({ type: 'SET_DEMO', on: false });
      return;
    }
    apiFetch<{ active: any[]; history: any[] }>('/api/execution/demo-sessions')
      .then(data => {
        const all = [...(data.active || []), ...(data.history || [])];
        dispatch({ type: 'SET_DEMO', on: true, sessions: all });
        setShowCompleted(true);
      })
      .catch(logCatch('ExecutionDashboard', 'toggleDemo'));
  }, [dispatch, state.demoOn]);

  const canClearFinished = completedSessions.length > 0;

  const filteredActive = activeSessions.filter(s =>
    !filterText ||
    (s.watchItemTag || '').toLowerCase().includes(filterText.toLowerCase()) ||
    (s.userId || '').toLowerCase().includes(filterText.toLowerCase())
  );

  // Only reserve space for the session panel when there is something to show.
  // When idle, the log panel fills the full height instead of leaving an empty box.
  const hasTopContent =
    filteredActive.length > 0 ||
    (showCompleted && completedSessions.length > 0);

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
          onClick={() => { reload().catch(logCatch('ExecutionDashboard', 'reload')); }}
          className="text-xs px-3 py-1 rounded border border-bdr text-text-secondary hover:text-text-primary transition-colors"
          title="Re-query the controller for current sessions"
        >
          &#128260; Reload
        </button>
        <button
          onClick={() => dispatch({ type: 'CLEAR_FINISHED' })}
          disabled={!canClearFinished}
          className="text-xs px-3 py-1 rounded border border-bdr text-text-secondary hover:text-text-primary transition-colors disabled:opacity-40 disabled:cursor-not-allowed"
          title="Hide completed, failed and cancelled pipelines. Running pipelines stay, and nothing is removed from History."
        >
          &#129529; Clear finished
        </button>
        <button
          onClick={toggleDemo}
          className={`text-xs px-3 py-1 rounded border transition-colors ${
            state.demoOn
              ? 'bg-accent/20 text-accent border-accent'
              : 'border-accent/30 text-accent bg-accent/10 hover:bg-accent/20'
          }`}
          title="Add or remove sample pipelines for testing the dashboard UI. Real runs are unaffected."
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

        {/* Panel 1: Session cards (only when there is content to show) */}
        {hasTopContent && (
          <div
            className="overflow-auto px-4 py-3"
            style={{ height: `${splitPercent}%` }}
          >
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
        )}

        {/* Splitter (only when the session panel is visible) */}
        {hasTopContent && (
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
        )}

        {/* Panel 2: Log (fills remaining space, or full height when idle) */}
        <div
          className="overflow-hidden"
          style={{ height: hasTopContent ? `${100 - splitPercent}%` : '100%' }}
        >
          <LogPanel />
        </div>
      </div>
    </div>
  );
}
