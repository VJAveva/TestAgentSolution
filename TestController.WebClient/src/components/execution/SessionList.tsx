import { useEffect, useMemo } from 'react';
import { useExecutionStore } from '../../stores/executionStore';
import { useAgentStore } from '../../stores/agentStore';
import { useExecution } from '../../hooks/useExecution';
import { agentColor } from '../../lib/agentColors';
import { logCatch } from '../../lib/logger';
import { PlayCircle, XCircle, X, RefreshCw } from 'lucide-react';

export default function SessionList() {
  const isExecuting = useExecutionStore(s => s.isExecuting);
  const activeCount = useExecutionStore(s => s.activeCount);
  const sessions = useExecutionStore(s => s.sessions);
  const logs = useExecutionStore(s => s.logs);
  const selectedAgent = useExecutionStore(s => s.selectedAgent);
  const setSelectedAgent = useExecutionStore(s => s.setSelectedAgent);
  const fleetAgents = useAgentStore(s => s.agents);
  const { triggerAll, cancelAll, cancelSession, fetchSessions } = useExecution();

  // Which agents have actually produced output, per session. Derived from the same live
  // stream the log renders, so the panel and the log can never disagree.
  const agentsBySession = useMemo(() => {
    const map = new Map<string, string[]>();
    for (const entry of logs) {
      if (!entry.sessionId || !entry.agent) continue;
      const seen = map.get(entry.sessionId);
      if (!seen) map.set(entry.sessionId, [entry.agent]);
      else if (!seen.includes(entry.agent)) seen.push(entry.agent);
    }
    return map;
  }, [logs]);

  // Fetch once on mount and whenever a run starts or ends. The poll below only runs while we
  // already believe a run is active, and only fetchSessions can set that flag - so on its own
  // it can never start for a run this browser did not trigger.
  useEffect(() => {
    const refresh = () => fetchSessions().catch(logCatch('SessionList', 'fetchSessions'));
    refresh();
    window.addEventListener('execution-sessions-changed', refresh);
    return () => window.removeEventListener('execution-sessions-changed', refresh);
  }, [fetchSessions]);

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

            const participating = agentsBySession.get(s.sessionId) ?? [];
            const notScheduled = fleetAgents
              .map(a => a.name)
              .filter(n => !participating.some(p => p.toLowerCase() === n.toLowerCase()));
            const sessionRunning = s.state === 'Running';

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

                {/* Agent nodes, flat under the root pipeline */}
                <div className="mt-2 pt-2 border-t border-bdr space-y-0.5">
                  <AgentNode
                    label="All agents"
                    sub="Combined live log"
                    running={sessionRunning}
                    badge={sessionRunning ? `${participating.length} RUN` : 'DONE'}
                    isSelected={selectedAgent === ''}
                    onSelect={() => setSelectedAgent('')}
                  />
                  {participating.map(a => (
                    <AgentNode
                      key={a}
                      label={a}
                      sub={sessionRunning ? 'Streaming live' : 'Finished'}
                      colorText={agentColor(a).text}
                      running={sessionRunning}
                      badge={sessionRunning ? 'RUNNING' : 'DONE'}
                      isSelected={selectedAgent === a}
                      onSelect={() => setSelectedAgent(a)}
                    />
                  ))}
                  {notScheduled.map(a => (
                    <AgentNode
                      key={a}
                      label={a}
                      sub="Not scheduled this run"
                      running={false}
                      badge="IDLE"
                      isSelected={selectedAgent === a}
                      onSelect={() => setSelectedAgent(a)}
                    />
                  ))}
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

function AgentNode({ label, sub, colorText, running, badge, isSelected, onSelect }: {
  label: string;
  sub: string;
  colorText?: string;
  running: boolean;
  badge: string;
  isSelected: boolean;
  onSelect: () => void;
}) {
  return (
    <button
      className={`w-full flex items-center gap-2 px-2 py-1.5 rounded text-left transition-colors ${
        isSelected ? 'bg-accent/15 border border-accent/40' : 'border border-transparent hover:bg-white/5'
      }`}
      onClick={onSelect}
    >
      <span className={`w-2 h-2 rounded-full shrink-0 ${running ? 'bg-acc-green' : 'bg-text-muted'}`} />
      <span className="flex-1 min-w-0">
        <span className={`block text-[11px] font-semibold font-mono truncate ${colorText ?? 'text-text-primary'}`}>
          {label}
        </span>
        <span className="block text-[9px] text-text-muted truncate">{sub}</span>
      </span>
      <span className={`text-[9px] font-bold px-1.5 py-0.5 rounded-full shrink-0 ${
        running ? 'bg-acc-green/15 text-acc-green' : 'bg-white/5 text-text-muted'
      }`}>
        {badge}
      </span>
    </button>
  );
}
