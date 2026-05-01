import { useEffect, useState, useCallback, useMemo, useRef } from 'react';
import { useExecutionStore } from '../../stores/executionStore';
import { useAgentStore } from '../../stores/agentStore';
import { useExecution } from '../../hooks/useExecution';
import { useAgents } from '../../hooks/useAgents';
import {
  Activity, RefreshCw, ChevronRight, Search, Filter,
  CheckCircle2, XCircle, Circle, Loader2, RotateCw, StopCircle,
} from 'lucide-react';
import type { SessionPipeline, AgentPipeline, PipelineAction, LogEntry } from '../../types/api';

type MonitorView = 'pipeline' | 'timeline' | 'log';
type StatusFilter = 'all' | 'running' | 'failed';

/** Forces a re-render every second so elapsed times tick live. */
function useTickingClock(enabled: boolean) {
  const [, setTick] = useState(0);
  useEffect(() => {
    if (!enabled) return;
    const id = setInterval(() => setTick(t => t + 1), 1000);
    return () => clearInterval(id);
  }, [enabled]);
}

export default function ExecutionMonitor() {
  const [activeView, setActiveView] = useState<MonitorView>('pipeline');
  const sessions = useExecutionStore(s => s.sessions);
  const isExecuting = useExecutionStore(s => s.isExecuting);
  const pipelines = useExecutionStore(s => s.pipelines);
  const logs = useExecutionStore(s => s.logs);
  const initSession = useExecutionStore(s => s.initSession);
  const { fetchSessions, cancelSession } = useExecution();
  const { fetchAgents } = useAgents();
  const [refreshing, setRefreshing] = useState(false);

  // Tick elapsed time every second while any session is running
  useTickingClock(isExecuting);

  // Fetch on mount and poll
  useEffect(() => {
    fetchSessions().catch(() => {});
    fetchAgents().catch(() => {});
  }, [fetchSessions, fetchAgents]);

  useEffect(() => {
    const interval = isExecuting ? 2000 : 5000;
    const id = setInterval(() => fetchSessions().catch(() => {}), interval);
    return () => clearInterval(id);
  }, [isExecuting, fetchSessions]);

  // Hydrate pipeline store from REST sessions on mount (for mid-execution navigation)
  const hydratedRef = useRef<Set<string>>(new Set());
  useEffect(() => {
    sessions.forEach(s => {
      if (s.state === 'Running' && !pipelines.has(s.sessionId) && !hydratedRef.current.has(s.sessionId)) {
        hydratedRef.current.add(s.sessionId);
        // Create a placeholder pipeline from REST data so it appears immediately
        initSession(s.sessionId, s.watchItemTag, '', [], s.startedUtc);
      }
    });
  }, [sessions, pipelines, initSession]);

  const handleRefresh = async () => {
    setRefreshing(true);
    await fetchSessions().catch(() => {});
    setRefreshing(false);
  };

  // Compute aggregate stats
  const pipelineArray = useMemo(() => Array.from(pipelines.values()), [pipelines]);
  const stats = useMemo(() => {
    const activeSessions = pipelineArray.filter(p => p.state === 'Running').length;
    const allAgents = pipelineArray.flatMap(p => p.agents);
    const lockedAgents = allAgents.filter(a => a.state === 'Executing' || a.state === 'Rebooting').length;
    const allActions = allAgents.flatMap(a => a.actions);
    const passed = allActions.filter(a => a.status === 'Success').length;
    const failed = allActions.filter(a => a.status === 'Failed' || a.status === 'TimedOut').length;
    const total = allActions.length;
    const progress = total > 0 ? Math.round(((passed + failed) / total) * 100) : 0;
    return { activeSessions, lockedAgents, passed, failed, progress };
  }, [pipelineArray]);

  // Merge pipeline data with session data from REST
  const mergedPipelines = useMemo(() => {
    // If we have pipeline data from SignalR, use it
    if (pipelineArray.length > 0) return pipelineArray;
    // Otherwise, synthesize from REST session data
    return sessions.map(s => ({
      sessionId: s.sessionId,
      watchItemTag: s.watchItemTag,
      state: s.state,
      startedUtc: s.startedUtc,
      agents: [] as AgentPipeline[],
      totalActions: s.totalActions,
      passedActions: s.passedActions,
      failedActions: s.failedActions,
      progressPercent: s.progressPercent,
    } satisfies SessionPipeline));
  }, [pipelineArray, sessions]);

  return (
    <div className="flex flex-col h-full overflow-hidden">
      {/* Tabs */}
      <div className="flex items-center border-b border-bdr px-4 shrink-0">
        <div className="flex gap-0.5">
          {(['pipeline', 'timeline', 'log'] as MonitorView[]).map(view => (
            <button
              key={view}
              onClick={() => setActiveView(view)}
              className={`px-4 py-2.5 text-xs font-medium border-b-2 transition-colors ${
                activeView === view
                  ? 'text-accent border-accent'
                  : 'text-text-secondary border-transparent hover:text-text-primary'
              }`}
            >
              {view === 'pipeline' ? 'Pipeline view' : view === 'timeline' ? 'Timeline view' : 'Unified log'}
            </button>
          ))}
        </div>
        <div className="ml-auto flex items-center gap-2">
          <button
            onClick={handleRefresh}
            disabled={refreshing}
            className="p-1.5 rounded hover:bg-white/10 text-text-muted hover:text-text-primary transition-colors"
          >
            <RefreshCw size={13} className={refreshing ? 'animate-spin' : ''} />
          </button>
        </div>
      </div>

      {/* Stats bar */}
      <div className="grid grid-cols-5 gap-2 px-4 py-3 shrink-0">
        <StatCard value={stats.activeSessions} label="Active sessions" color="text-accent" />
        <StatCard value={stats.lockedAgents} label="Agents locked" color="text-text-primary" />
        <StatCard value={stats.passed} label="Actions passed" color="text-acc-green" />
        <StatCard value={stats.failed} label="Actions failed" color="text-acc-red" />
        <StatCard value={`${stats.progress}%`} label="Overall progress" color="text-text-primary" />
      </div>

      {/* View content */}
      <div className="flex-1 overflow-auto px-4 pb-4">
        {activeView === 'pipeline' && <PipelineView pipelines={mergedPipelines} />}
        {activeView === 'timeline' && <TimelineView pipelines={mergedPipelines} />}
        {activeView === 'log' && <UnifiedLogView logs={logs} pipelines={mergedPipelines} />}
      </div>
    </div>
  );
}

// ═══ STAT CARD ═══
function StatCard({ value, label, color }: { value: number | string; label: string; color: string }) {
  return (
    <div className="bg-bg-card rounded-lg p-3 text-center border border-bdr">
      <div className={`text-xl font-medium ${color}`}>{value}</div>
      <div className="text-[10px] text-text-muted mt-0.5">{label}</div>
    </div>
  );
}

// ═══ PIPELINE VIEW ═══
function PipelineView({ pipelines }: { pipelines: SessionPipeline[] }) {
  const [filter, setFilter] = useState('');
  const [statusFilter, setStatusFilter] = useState<StatusFilter>('all');
  const [expanded, setExpanded] = useState<Set<string>>(new Set());

  // Auto-expand running sessions
  useEffect(() => {
    const running = pipelines.filter(p => p.state === 'Running').map(p => p.sessionId);
    setExpanded(prev => {
      const next = new Set(prev);
      running.forEach(id => next.add(id));
      return next;
    });
  }, [pipelines]);

  const filtered = useMemo(() => {
    let result = pipelines;
    if (statusFilter === 'running') result = result.filter(p => p.state === 'Running');
    if (statusFilter === 'failed') result = result.filter(p => p.state === 'Failed' || p.state === 'PartialFailure');
    if (filter) {
      const lower = filter.toLowerCase();
      result = result.filter(p =>
        p.watchItemTag.toLowerCase().includes(lower) ||
        p.agents.some(a => a.agentName.toLowerCase().includes(lower))
      );
    }
    return result;
  }, [pipelines, filter, statusFilter]);

  const toggleExpand = (id: string) => {
    setExpanded(prev => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });
  };

  if (pipelines.length === 0) {
    return (
      <div className="flex flex-col items-center justify-center py-16 text-text-muted">
        <Activity size={32} className="mb-3 opacity-30" />
        <p className="text-sm font-medium">No active executions</p>
        <p className="text-xs mt-1">Trigger a WatchItem to see the pipeline monitor here.</p>
      </div>
    );
  }

  return (
    <div className="space-y-3">
      {/* Search + filter */}
      <div className="flex gap-2 items-center">
        <div className="relative flex-1">
          <Search size={12} className="absolute left-2.5 top-2 text-text-muted" />
          <input
            type="text"
            value={filter}
            onChange={e => setFilter(e.target.value)}
            placeholder="Filter by session, agent, or action..."
            className="w-full pl-7 pr-3 py-1.5 text-xs rounded bg-bg-card border border-bdr text-text-primary placeholder:text-text-muted focus:outline-none focus:border-accent"
          />
        </div>
        <div className="flex gap-0.5 bg-bg-card rounded border border-bdr p-0.5">
          {(['all', 'running', 'failed'] as StatusFilter[]).map(sf => (
            <button
              key={sf}
              onClick={() => setStatusFilter(sf)}
              className={`px-2.5 py-1 text-[10px] rounded font-medium transition-colors ${
                statusFilter === sf
                  ? 'bg-bg text-text-primary'
                  : 'text-text-muted hover:text-text-primary'
              }`}
            >
              {sf.charAt(0).toUpperCase() + sf.slice(1)}
            </button>
          ))}
        </div>
      </div>

      {/* Session cards */}
      {filtered.map(pipeline => (
        <SessionPipelineCard
          key={pipeline.sessionId}
          pipeline={pipeline}
          isExpanded={expanded.has(pipeline.sessionId)}
          onToggle={() => toggleExpand(pipeline.sessionId)}
        />
      ))}
    </div>
  );
}

function SessionPipelineCard({ pipeline, isExpanded, onToggle }: { pipeline: SessionPipeline; isExpanded: boolean; onToggle: () => void }) {
  const { cancelSession } = useExecution();
  const [cancelling, setCancelling] = useState(false);
  const isRunning = pipeline.state === 'Running';
  const isFailed = pipeline.state === 'Failed' || pipeline.state === 'PartialFailure';
  const isComplete = pipeline.state === 'Completed' || pipeline.state === 'Success';
  const badgeClass =
    isRunning ? 'bg-accent/15 text-accent'
    : isComplete ? 'bg-acc-green/15 text-acc-green'
    : isFailed ? 'bg-acc-red/15 text-acc-red'
    : 'bg-bg-surface text-text-muted';

  const elapsed = pipeline.startedUtc ? formatElapsed(pipeline.startedUtc) : '--';

  const handleCancel = async (e: React.MouseEvent) => {
    e.stopPropagation();
    setCancelling(true);
    try { await cancelSession(pipeline.sessionId); } catch {}
    setCancelling(false);
  };

  // Compute banner summary
  const agentsDone = pipeline.agents.filter(a => a.state === 'Done').length;
  const agentsFailed = pipeline.agents.filter(a => a.state === 'Failed').length;

  return (
    <div className="bg-bg-card border border-bdr rounded-lg overflow-hidden">
      {/* Header */}
      <div
        className="flex items-center justify-between px-4 py-3 cursor-pointer hover:bg-white/[0.02] transition-colors"
        onClick={onToggle}
      >
        <div className="flex items-center gap-2">
          <ChevronRight size={12} className={`text-text-muted transition-transform ${isExpanded ? 'rotate-90' : ''}`} />
          <span className={`text-[10px] font-medium px-2 py-0.5 rounded ${badgeClass}`}>{pipeline.state}</span>
          <span className="text-sm font-medium text-text-primary">{pipeline.watchItemTag || pipeline.sessionId.slice(0, 8)}</span>
        </div>
        <div className="flex items-center gap-3 text-[11px] text-text-muted">
          {pipeline.userId && (
            <span className="font-mono text-[9px] bg-bg-surface px-1.5 py-0.5 rounded">{pipeline.userId}</span>
          )}
          <span>{pipeline.agents.length} agent{pipeline.agents.length !== 1 ? 's' : ''}</span>
          <span>{elapsed}</span>
          <span>{pipeline.progressPercent}%</span>
          {isRunning && (
            <button
              onClick={handleCancel}
              disabled={cancelling}
              title="Cancel session"
              className="ml-1 p-1 rounded hover:bg-acc-red/15 text-text-muted hover:text-acc-red transition-colors"
            >
              <StopCircle size={13} className={cancelling ? 'animate-pulse' : ''} />
            </button>
          )}
        </div>
      </div>

      {/* Result banner for completed/failed sessions */}
      {isExpanded && isComplete && pipeline.agents.length > 0 && (
        <div className="flex items-center gap-2 px-4 py-2 bg-acc-green/5 border-t border-acc-green/20 text-[11px] text-acc-green">
          <CheckCircle2 size={12} />
          <span>
            All {pipeline.agents.length} agent{pipeline.agents.length !== 1 ? 's' : ''} completed successfully.
            {pipeline.passedActions > 0 && ` ${pipeline.passedActions} actions passed`}
            {pipeline.failedActions > 0 && `, ${pipeline.failedActions} failed`}
            {pipeline.passedActions > 0 && pipeline.failedActions === 0 && ', 0 failed'}
            .
          </span>
        </div>
      )}
      {isExpanded && isFailed && pipeline.agents.length > 0 && (
        <div className="flex items-center gap-2 px-4 py-2 bg-acc-red/5 border-t border-acc-red/20 text-[11px] text-acc-red">
          <XCircle size={12} />
          <span>
            {agentsFailed} agent{agentsFailed !== 1 ? 's' : ''} failed.
            {agentsDone > 0 && ` ${agentsDone} agent${agentsDone !== 1 ? 's' : ''} completed successfully.`}
          </span>
        </div>
      )}

      {/* Body - agent rows */}
      {isExpanded && pipeline.agents.length > 0 && (
        <div className="border-t border-bdr">
          {pipeline.agents.map(agent => (
            <AgentRow key={agent.agentName} agent={agent} />
          ))}
        </div>
      )}

      {/* Body - no agent detail available */}
      {isExpanded && pipeline.agents.length === 0 && (
        <div className="border-t border-bdr px-4 py-3 text-center text-xs text-text-muted">
          {isRunning ? 'Waiting for action progress events...' : `${pipeline.passedActions} passed, ${pipeline.failedActions} failed of ${pipeline.totalActions} actions`}
        </div>
      )}
    </div>
  );
}

function AgentRow({ agent }: { agent: AgentPipeline }) {
  const stateColor =
    agent.state === 'Executing' ? 'text-accent'
    : agent.state === 'Done' ? 'text-acc-green'
    : agent.state === 'Failed' ? 'text-acc-red'
    : agent.state === 'Rebooting' ? 'text-acc-yellow'
    : 'text-text-muted';

  const barColor =
    agent.state === 'Executing' ? 'bg-accent'
    : agent.state === 'Done' ? 'bg-acc-green'
    : agent.state === 'Failed' ? 'bg-acc-red'
    : 'bg-bg-surface';

  return (
    <div className="flex items-stretch border-b border-bdr/30 last:border-b-0">
      {/* Agent name column */}
      <div className="w-[120px] shrink-0 px-4 py-2.5 flex flex-col justify-center border-r border-bdr/30 bg-bg-panel">
        <div className="font-mono text-xs font-medium text-text-primary">{agent.agentName}</div>
        <div className={`text-[10px] mt-0.5 ${stateColor}`}>{agent.state}</div>
        <div className="h-[3px] bg-bg-surface rounded-full mt-1.5 overflow-hidden">
          <div className={`h-full rounded-full transition-all duration-500 ${barColor}`} style={{ width: `${agent.progressPercent}%` }} />
        </div>
      </div>

      {/* Pipeline actions */}
      <div className="flex-1 px-3 py-2 flex flex-wrap items-center gap-1">
        {agent.actions.map((action, idx) => (
          <span key={action.actionTag}>
            {idx > 0 && <span className="text-text-muted/40 text-[9px] mx-0.5">→</span>}
            <ActionChip action={action} />
          </span>
        ))}
        {agent.actions.length === 0 && (
          <span className="text-[10px] text-text-muted italic">Waiting...</span>
        )}
      </div>
    </div>
  );
}

function ActionChip({ action }: { action: PipelineAction }) {
  const isDone = action.status === 'Success';
  const isRunning = action.status === 'Running';
  const isFailed = action.status === 'Failed' || action.status === 'TimedOut';
  const isRebooting = action.status === 'Rebooting';
  const isPending = action.status === 'Pending';

  const chipClass = isDone
    ? 'bg-acc-green/10 border-acc-green/30 text-acc-green'
    : isRunning
      ? 'bg-accent/10 border-accent/30 text-accent'
      : isFailed
        ? 'bg-acc-red/10 border-acc-red/30 text-acc-red'
        : isRebooting
          ? 'bg-acc-yellow/10 border-acc-yellow/30 text-acc-yellow'
          : 'border-bdr text-text-muted';

  const icon = isDone
    ? <CheckCircle2 size={10} />
    : isRunning
      ? <Loader2 size={10} className="animate-spin" />
      : isFailed
        ? <XCircle size={10} />
        : isRebooting
          ? <RotateCw size={10} />
          : <Circle size={10} />;

  const label = action.command || action.actionTag;
  const progress = isRunning && action.progressPercent ? ` (${action.progressPercent}%)` : '';

  return (
    <span className={`inline-flex items-center gap-1 text-[11px] px-2 py-0.5 rounded border ${chipClass} max-w-[180px] overflow-hidden`}>
      {icon}
      <span className="truncate">{label}{progress}</span>
    </span>
  );
}

// ═══ TIMELINE VIEW ═══
function TimelineView({ pipelines }: { pipelines: SessionPipeline[] }) {
  const runningPipelines = pipelines.filter(p => p.agents.length > 0);

  if (runningPipelines.length === 0) {
    return (
      <div className="flex flex-col items-center justify-center py-16 text-text-muted">
        <Activity size={32} className="mb-3 opacity-30" />
        <p className="text-sm">Timeline view requires active sessions with action progress data.</p>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      {runningPipelines.map(pipeline => (
        <div key={pipeline.sessionId} className="bg-bg-card border border-bdr rounded-lg p-4">
          <h3 className="text-xs font-semibold text-text-primary mb-3">{pipeline.watchItemTag || pipeline.sessionId.slice(0, 8)}</h3>
          <div className="space-y-1.5">
            {/* Time axis */}
            <div className="flex items-center gap-1 text-[9px] text-text-muted font-mono ml-[70px]">
              <span>0:00</span>
              <span className="flex-1 border-b border-dashed border-bdr/30" />
              <span>now</span>
            </div>
            {pipeline.agents.map(agent => (
              <TimelineAgentRow key={agent.agentName} agent={agent} sessionStart={pipeline.startedUtc} />
            ))}
          </div>
        </div>
      ))}
    </div>
  );
}

function TimelineAgentRow({ agent, sessionStart }: { agent: AgentPipeline; sessionStart: string }) {
  const sessionStartMs = new Date(sessionStart).getTime();
  const nowMs = Date.now();
  const totalDuration = Math.max(nowMs - sessionStartMs, 1);

  return (
    <div className="flex items-center gap-2">
      <span className="w-[60px] text-[10px] font-mono text-text-secondary truncate shrink-0">{agent.agentName}</span>
      <div className="flex-1 flex gap-[2px] h-[18px] rounded overflow-hidden bg-bg-panel">
        {agent.actions.map(action => {
          const actionStartMs = action.startedUtc ? new Date(action.startedUtc).getTime() : sessionStartMs;
          const widthPercent = Math.max(2, ((nowMs - actionStartMs) / totalDuration) * 100 / agent.actions.length);

          const bgColor = action.status === 'Success' ? 'bg-acc-green/30'
            : action.status === 'Running' ? 'bg-accent/30'
            : action.status === 'Failed' || action.status === 'TimedOut' ? 'bg-acc-red/30'
            : action.status === 'Rebooting' ? 'bg-acc-yellow/30'
            : 'bg-bg-surface';

          const textColor = action.status === 'Success' ? 'text-acc-green'
            : action.status === 'Running' ? 'text-accent'
            : action.status === 'Failed' ? 'text-acc-red'
            : 'text-text-muted';

          return (
            <div
              key={action.actionTag}
              className={`${bgColor} flex items-center px-1 rounded-sm overflow-hidden`}
              style={{ flex: action.status === 'Pending' ? 0.5 : 1 }}
              title={`${action.command} - ${action.status}`}
            >
              <span className={`text-[9px] ${textColor} truncate`}>
                {action.status === 'Running' ? '●' : action.status === 'Success' ? '✓' : action.status === 'Failed' ? '✗' : ''} {action.command}
              </span>
            </div>
          );
        })}
      </div>
    </div>
  );
}

// ═══ UNIFIED LOG VIEW ═══
function UnifiedLogView({ logs, pipelines }: { logs: LogEntry[]; pipelines: SessionPipeline[] }) {
  const [search, setSearch] = useState('');
  const [sessionFilter, setSessionFilter] = useState('');
  const [levelFilter, setLevelFilter] = useState<'all' | 'error'>('all');
  const [agentFilter, setAgentFilter] = useState('');

  // Get unique agents from logs
  const agents = useMemo(() => {
    const set = new Set<string>();
    logs.forEach(l => { if (l.agent) set.add(l.agent); });
    return Array.from(set).sort();
  }, [logs]);

  const sessionNames = useMemo(() => {
    return pipelines.map(p => ({ id: p.sessionId, name: p.watchItemTag }));
  }, [pipelines]);

  const filtered = useMemo(() => {
    let result = [...logs].reverse(); // newest first
    if (levelFilter === 'error') result = result.filter(l => l.severity === 'error');
    if (sessionFilter) result = result.filter(l => l.sessionId === sessionFilter);
    if (agentFilter) result = result.filter(l => l.agent === agentFilter);
    if (search) {
      const lower = search.toLowerCase();
      result = result.filter(l => l.message.toLowerCase().includes(lower));
    }
    return result.slice(0, 500);
  }, [logs, search, sessionFilter, levelFilter, agentFilter]);

  return (
    <div className="space-y-2">
      {/* Filters */}
      <div className="flex gap-2 items-center">
        <div className="relative flex-1">
          <Search size={12} className="absolute left-2.5 top-2 text-text-muted" />
          <input
            type="text"
            value={search}
            onChange={e => setSearch(e.target.value)}
            placeholder="Search logs..."
            className="w-full pl-7 pr-3 py-1.5 text-xs rounded bg-bg-card border border-bdr text-text-primary placeholder:text-text-muted focus:outline-none focus:border-accent"
          />
        </div>
        <select
          value={sessionFilter}
          onChange={e => setSessionFilter(e.target.value)}
          className="px-2 py-1.5 text-xs rounded bg-bg-card border border-bdr text-text-primary"
        >
          <option value="">All sessions</option>
          {sessionNames.map(s => (
            <option key={s.id} value={s.id}>{s.name || s.id.slice(0, 8)}</option>
          ))}
        </select>
        <select
          value={levelFilter}
          onChange={e => setLevelFilter(e.target.value as 'all' | 'error')}
          className="px-2 py-1.5 text-xs rounded bg-bg-card border border-bdr text-text-primary"
        >
          <option value="all">All levels</option>
          <option value="error">Errors only</option>
        </select>
      </div>

      {/* Agent filter pills */}
      {agents.length > 0 && (
        <div className="flex gap-1 flex-wrap">
          <button
            onClick={() => setAgentFilter('')}
            className={`text-[10px] px-2.5 py-1 rounded-full border transition-colors ${
              !agentFilter ? 'bg-accent/15 border-accent/30 text-accent' : 'border-bdr text-text-muted hover:text-text-primary'
            }`}
          >
            All
          </button>
          {agents.slice(0, 10).map(a => (
            <button
              key={a}
              onClick={() => setAgentFilter(agentFilter === a ? '' : a)}
              className={`text-[10px] px-2.5 py-1 rounded-full border transition-colors ${
                agentFilter === a ? 'bg-accent/15 border-accent/30 text-accent' : 'border-bdr text-text-muted hover:text-text-primary'
              }`}
            >
              {a}
            </button>
          ))}
        </div>
      )}

      {/* Log entries */}
      <div className="bg-bg-card border border-bdr rounded-lg overflow-hidden max-h-[calc(100vh-320px)] overflow-y-auto">
        {filtered.length === 0 ? (
          <div className="px-4 py-8 text-center text-xs text-text-muted">No log entries yet.</div>
        ) : (
          filtered.map((entry, idx) => (
            <LogRow key={idx} entry={entry} sessionNames={sessionNames} />
          ))
        )}
      </div>
    </div>
  );
}

function LogRow({ entry, sessionNames }: { entry: LogEntry; sessionNames: { id: string; name: string }[] }) {
  const ts = entry.timestamp ? new Date(entry.timestamp).toLocaleTimeString('en-GB', { hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit' }) : '';
  const sessionName = sessionNames.find(s => s.id === entry.sessionId)?.name;

  const sessionBadgeClass =
    entry.severity === 'error' ? 'bg-acc-red/15 text-acc-red'
    : entry.severity === 'success' ? 'bg-acc-green/15 text-acc-green'
    : 'bg-accent/15 text-accent';

  const msgClass = entry.severity === 'error' ? 'text-acc-red' : 'text-text-secondary';

  return (
    <div className="flex items-center px-3 py-1 text-[11px] font-mono border-b border-bdr/20 last:border-b-0 hover:bg-white/[0.02] gap-2">
      <span className="text-text-muted w-[55px] shrink-0">{ts}</span>
      {sessionName && (
        <span className={`text-[9px] px-1.5 py-0.5 rounded shrink-0 ${sessionBadgeClass}`}>
          {sessionName.slice(0, 8)}
        </span>
      )}
      <span className="text-accent w-[60px] shrink-0 truncate">{entry.agent || ''}</span>
      <span className={`flex-1 truncate ${msgClass}`}>{entry.message}</span>
    </div>
  );
}

// ═══ HELPERS ═══
function formatElapsed(startedUtc: string): string {
  const ms = Date.now() - new Date(startedUtc).getTime();
  if (ms < 0) return '--';
  const totalSec = Math.floor(ms / 1000);
  const h = Math.floor(totalSec / 3600);
  const m = Math.floor((totalSec % 3600) / 60);
  const s = totalSec % 60;
  return `${h.toString().padStart(2, '0')}:${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`;
}
