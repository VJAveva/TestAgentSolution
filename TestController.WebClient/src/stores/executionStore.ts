import { create } from 'zustand';
import type { LogEntry, SessionInfo, PipelineAction, AgentPipeline, SessionPipeline, AgentHeartbeat } from '../types/api';

interface ExecutionState {
  isExecuting: boolean;
  activeCount: number;
  sessions: SessionInfo[];
  logs: LogEntry[];
  maxLogs: number;
  isLogPaused: boolean;
  logSessionFilter: string;

  // Pipeline monitoring state
  pipelines: Map<string, SessionPipeline>;
  agentHeartbeats: Map<string, AgentHeartbeat>;

  setStatus: (isExecuting: boolean, activeCount: number) => void;
  setSessions: (sessions: SessionInfo[]) => void;
  addLog: (entry: LogEntry) => void;
  clearLogs: () => void;
  togglePause: () => void;
  setLogSessionFilter: (sessionId: string) => void;

  // Pipeline actions
  initSession: (sessionId: string, watchItemTag: string, userId: string, lockedAgents: string[], startedUtc: string) => void;
  updateActionProgress: (sessionId: string, agentName: string, actionTag: string, command: string, status: string, progressPercent?: number) => void;
  completeSession: (sessionId: string, state: string) => void;
  updateHeartbeat: (heartbeat: AgentHeartbeat) => void;
  clearPipelines: () => void;
}

function computeAgentState(actions: PipelineAction[]): AgentPipeline['state'] {
  if (actions.some(a => a.status === 'Rebooting')) return 'Rebooting';
  if (actions.some(a => a.status === 'Running')) return 'Executing';
  if (actions.some(a => a.status === 'Failed' || a.status === 'TimedOut')) return 'Failed';
  if (actions.length > 0 && actions.every(a => a.status === 'Success')) return 'Done';
  if (actions.some(a => a.status === 'Success')) return 'Executing';
  return 'Idle';
}

function computeAgentProgress(actions: PipelineAction[]): number {
  if (actions.length === 0) return 0;
  const done = actions.filter(a => a.status === 'Success' || a.status === 'Failed' || a.status === 'TimedOut').length;
  const running = actions.find(a => a.status === 'Running');
  const runningContrib = running?.progressPercent ? (running.progressPercent / 100) : 0;
  return Math.round(((done + runningContrib) / actions.length) * 100);
}

export const useExecutionStore = create<ExecutionState>((set) => ({
  isExecuting: false,
  activeCount: 0,
  sessions: [],
  logs: [],
  maxLogs: 2000,
  isLogPaused: false,
  logSessionFilter: '',
  pipelines: new Map(),
  agentHeartbeats: new Map(),

  setStatus: (isExecuting, activeCount) => set({ isExecuting, activeCount }),
  setSessions: (sessions) => set({ sessions }),
  addLog: (entry) =>
    set((s) => {
      if (s.isLogPaused) return s;
      const logs = [...s.logs, entry];
      if (logs.length > s.maxLogs) logs.splice(0, logs.length - s.maxLogs);
      return { logs };
    }),
  clearLogs: () => set({ logs: [] }),
  togglePause: () => set((s) => ({ isLogPaused: !s.isLogPaused })),
  setLogSessionFilter: (sessionId) => set({ logSessionFilter: sessionId }),

  initSession: (sessionId, watchItemTag, userId, lockedAgents, startedUtc) =>
    set((s) => {
      const pipelines = new Map(s.pipelines);
      pipelines.set(sessionId, {
        sessionId,
        watchItemTag,
        state: 'Running',
        userId,
        startedUtc,
        agents: lockedAgents.map(name => ({ agentName: name, state: 'Idle', actions: [], progressPercent: 0 })),
        totalActions: 0,
        passedActions: 0,
        failedActions: 0,
        progressPercent: 0,
      });
      return { pipelines };
    }),

  updateActionProgress: (sessionId, agentName, actionTag, command, status, progressPercent) =>
    set((s) => {
      const pipelines = new Map(s.pipelines);
      const session = pipelines.get(sessionId);
      if (!session) {
        // Auto-create session if we get progress without ExecutionStarted
        const newSession: SessionPipeline = {
          sessionId,
          watchItemTag: '',
          state: 'Running',
          startedUtc: new Date().toISOString(),
          agents: [{ agentName, state: 'Executing', actions: [], progressPercent: 0 }],
          totalActions: 0,
          passedActions: 0,
          failedActions: 0,
          progressPercent: 0,
        };
        pipelines.set(sessionId, newSession);
      }
      const pipeline = pipelines.get(sessionId)!;

      // Find or create agent
      let agent = pipeline.agents.find(a => a.agentName === agentName);
      if (!agent) {
        agent = { agentName, state: 'Idle', actions: [], progressPercent: 0 };
        pipeline.agents.push(agent);
      }

      // Find or create action
      let action = agent.actions.find(a => a.actionTag === actionTag);
      const mappedStatus = status === 'Running' ? 'Running'
        : status === 'Success' ? 'Success'
        : status === 'Failed' ? 'Failed'
        : status === 'Cancelled' ? 'Cancelled'
        : status === 'TimedOut' ? 'TimedOut'
        : status === 'Rebooting' ? 'Rebooting'
        : 'Pending';

      if (!action) {
        action = {
          actionTag,
          command: command || actionTag,
          agentName,
          status: mappedStatus,
          startedUtc: mappedStatus === 'Running' ? new Date().toISOString() : undefined,
          progressPercent,
        };
        agent.actions.push(action);
      } else {
        action.status = mappedStatus;
        if (progressPercent !== undefined) action.progressPercent = progressPercent;
        if (mappedStatus === 'Running' && !action.startedUtc) action.startedUtc = new Date().toISOString();
      }

      // Recompute agent state and progress
      agent.state = computeAgentState(agent.actions);
      agent.progressPercent = computeAgentProgress(agent.actions);

      // Recompute session totals
      const allActions = pipeline.agents.flatMap(a => a.actions);
      pipeline.totalActions = allActions.length;
      pipeline.passedActions = allActions.filter(a => a.status === 'Success').length;
      pipeline.failedActions = allActions.filter(a => a.status === 'Failed' || a.status === 'TimedOut').length;
      pipeline.progressPercent = allActions.length > 0
        ? Math.round((pipeline.passedActions + pipeline.failedActions) / allActions.length * 100)
        : 0;

      return { pipelines };
    }),

  completeSession: (sessionId, state) =>
    set((s) => {
      const pipelines = new Map(s.pipelines);
      const session = pipelines.get(sessionId);
      if (session) {
        session.state = state;
      }
      return { pipelines };
    }),

  updateHeartbeat: (heartbeat) =>
    set((s) => {
      const agentHeartbeats = new Map(s.agentHeartbeats);
      agentHeartbeats.set(heartbeat.agentName, heartbeat);
      return { agentHeartbeats };
    }),

  clearPipelines: () => set({ pipelines: new Map(), agentHeartbeats: new Map() }),
}));
