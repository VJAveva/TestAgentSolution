import { useEffect } from 'react';
import { useExecutionStore } from '../../stores/executionStore';
import { useAgentStore } from '../../stores/agentStore';
import { useExecution } from '../../hooks/useExecution';
import { logCatch } from '../../lib/logger';
import type { SessionInfo, AgentInfo } from '../../types/api';

export default function ExecutionMonitor() {
  const sessions = useExecutionStore(s => s.sessions);
  const isExecuting = useExecutionStore(s => s.isExecuting);
  const agents = useAgentStore(s => s.agents);
  const { fetchSessions } = useExecution();

  // Poll sessions every 2s while executing
  useEffect(() => {
    if (!isExecuting) return;
    const id = setInterval(() => fetchSessions().catch(logCatch('ExecutionMonitor', 'fetchSessions')), 2000);
    return () => clearInterval(id);
  }, [isExecuting, fetchSessions]);

  const runningSessions = sessions.filter(s => s.state === 'Running');
  const completedSessions = sessions.filter(s => s.state !== 'Running');
  const activeAgents = agents.filter(a => a.status === 'Busy' || a.status === 'Running');

  return (
    <div className="p-4 space-y-4 h-full overflow-auto">
      {/* Active sessions */}
      {runningSessions.length > 0 ? (
        <div className="space-y-3">
          <h2 className="text-xs font-bold text-text-secondary uppercase tracking-wider">
            Active Sessions ({runningSessions.length})
          </h2>
          {runningSessions.map(session => (
            <SessionCard key={session.sessionId} session={session} />
          ))}
        </div>
      ) : (
        <div className="flex flex-col items-center justify-center py-16 text-text-muted">
          <span className="text-3xl mb-3 opacity-50">●</span>
          <p className="text-sm font-medium">Idle</p>
          <p className="text-xs mt-1">No active executions. Trigger a WatchItem to see progress here.</p>
        </div>
      )}

      {/* Active agents */}
      {activeAgents.length > 0 && (
        <div className="space-y-2">
          <h2 className="text-xs font-bold text-text-secondary uppercase tracking-wider">
            Active Agents ({activeAgents.length})
          </h2>
          <div className="grid grid-cols-2 gap-2">
            {activeAgents.map(agent => (
              <AgentCard key={agent.name} agent={agent} />
            ))}
          </div>
        </div>
      )}

      {/* Recently completed sessions */}
      {completedSessions.length > 0 && (
        <div className="space-y-2">
          <h2 className="text-xs font-bold text-text-secondary uppercase tracking-wider">
            Recent Sessions ({completedSessions.length})
          </h2>
          {completedSessions.slice(0, 10).map(session => (
            <SessionCard key={session.sessionId} session={session} />
          ))}
        </div>
      )}
    </div>
  );
}

function SessionCard({ session }: { session: SessionInfo }) {
  const isRunning = session.state === 'Running';
  const stateColor =
    session.state === 'Running' ? 'text-accent'
    : session.state === 'Completed' ? 'text-acc-green'
    : session.state === 'Failed' || session.state === 'PartialFailure' ? 'text-acc-red'
    : 'text-text-muted';
  const barColor =
    session.state === 'Running' ? 'bg-accent'
    : session.state === 'Completed' ? 'bg-acc-green'
    : 'bg-acc-red';

  return (
    <div className="bg-bg-card rounded-lg p-4 border border-bdr">
      {/* Header */}
      <div className="flex items-center justify-between mb-3">
        <div className="flex items-center gap-2">
          {isRunning && <span className="w-2 h-2 rounded-full bg-accent animate-pulse" />}
          <span className="text-sm font-semibold text-text-primary">{session.watchItemTag}</span>
          <span className={`text-xs font-bold ${stateColor}`}>{session.state}</span>
        </div>
        <span className="text-[10px] text-text-muted font-mono">[{session.sessionId.slice(0, 8)}]</span>
      </div>

      {/* Progress bar */}
      <div className="h-2 bg-bg-surface rounded-full overflow-hidden mb-3">
        <div
          className={`h-full rounded-full transition-all duration-500 ${barColor}`}
          style={{ width: `${Math.min(100, session.progressPercent)}%` }}
        />
      </div>

      {/* Stats row */}
      <div className="flex items-center gap-4 text-xs text-text-muted font-mono">
        <span className="text-text-primary font-bold">{session.progressPercent.toFixed(0)}%</span>
        <span>{session.completedActions}/{session.totalActions} actions</span>
        <span className="text-acc-green">{session.passedActions} pass</span>
        {session.failedActions > 0 && (
          <span className="text-acc-red">{session.failedActions} fail</span>
        )}
        <span className="ml-auto text-[10px]">
          {new Date(session.startedUtc).toLocaleTimeString('en-GB', { hour12: false })}
        </span>
      </div>
    </div>
  );
}

function AgentCard({ agent }: { agent: AgentInfo }) {
  const statusColor =
    agent.status === 'Online' || agent.status === 'Idle' ? 'bg-acc-green'
    : agent.status === 'Busy' || agent.status === 'Running' ? 'bg-accent animate-pulse'
    : agent.status === 'Offline' ? 'bg-acc-red'
    : 'bg-text-muted';

  return (
    <div className="bg-bg-card rounded-lg p-3 border border-bdr">
      <div className="flex items-center gap-2">
        <span className={`w-2 h-2 rounded-full ${statusColor}`} />
        <span className="text-xs font-semibold text-text-primary">{agent.name}</span>
        <span className="text-[10px] text-text-muted ml-auto">{agent.status}</span>
      </div>
      {agent.lastStatusDetail && (
        <p className="text-[10px] text-text-muted mt-1 truncate font-mono">{agent.lastStatusDetail}</p>
      )}
    </div>
  );
}
