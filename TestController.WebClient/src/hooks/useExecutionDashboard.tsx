import React, {
  createContext, useContext, useReducer,
  useCallback, useEffect, type ReactNode, type Dispatch
} from 'react';
import { useConnectionStore, type SignalRStatus } from '../stores/connectionStore';
import { joinSession } from './useSignalR';
import { apiFetch } from '../lib/api';
import { logCatch } from '../lib/logger';
import type {
  SessionSummary, ActionExecution,
  DashboardLogEntry, ActionStatus, SessionStatus
} from '../types/execution';

// ── State shape ──
interface ExecutionDashboardState {
  sessions: Map<string, SessionSummary>;
  logs: DashboardLogEntry[];
  selectedSessionId: string | null;
  selectedAgentName: string | null;
  maxLogs: number;
}

const initialState: ExecutionDashboardState = {
  sessions: new Map(),
  logs: [],
  selectedSessionId: null,
  selectedAgentName: null,
  maxLogs: 10000,
};

// ── Actions ──
type Action =
  | { type: 'SET_SESSIONS'; sessions: SessionSummary[] }
  | { type: 'EXECUTION_STARTED'; data: any }
  | { type: 'EXECUTION_COMPLETED'; data: any }
  | { type: 'ACTION_PROGRESS'; data: any }
  | { type: 'AGENT_OUTPUT'; data: any }
  | { type: 'LOG_ENTRY'; data: DashboardLogEntry }
  | { type: 'SELECT_SESSION'; sessionId: string | null }
  | { type: 'SELECT_AGENT'; agentName: string | null }
  | { type: 'CLEAR_LOGS' };

function reducer(state: ExecutionDashboardState, action: Action): ExecutionDashboardState {
  switch (action.type) {
    case 'SET_SESSIONS': {
      const map = new Map<string, SessionSummary>();
      for (const s of action.sessions) {
        map.set(s.sessionId, { ...s, agents: s.agents || [] });
      }
      return { ...state, sessions: map };
    }

    case 'EXECUTION_STARTED': {
      const d = action.data;
      const session: SessionSummary = {
        sessionId: d.sessionId,
        watchItemTag: d.watchItemTag || '',
        userId: d.userId || '',
        source: d.source || 'WebClient',
        status: 'Running',
        startedUtc: d.startTime || new Date().toISOString(),
        elapsed: '00:00',
        agents: d.agents || [],
        totalActions: d.totalActions || 0,
        completedActions: 0,
        passedActions: 0,
        failedActions: 0,
        progressPercent: 0,
        lockedAgents: d.lockedAgents || [],
        buildNumber: d.buildNumber,
      };
      const next = new Map(state.sessions);
      next.set(session.sessionId, session);
      return { ...state, sessions: next };
    }

    case 'EXECUTION_COMPLETED': {
      const d = action.data;
      const next = new Map(state.sessions);
      const existing = next.get(d.sessionId);
      if (existing) {
        next.set(d.sessionId, {
          ...existing,
          status: d.state as SessionStatus,
          passedActions: d.passed > 0 ? d.passed : existing.passedActions,
          failedActions: d.failed > 0 ? d.failed : existing.failedActions,
          elapsed: d.totalDuration ?? existing.elapsed,
          progressPercent: 100,
        });
      }
      return { ...state, sessions: next };
    }

    case 'ACTION_PROGRESS': {
      const d = action.data;
      const next = new Map(state.sessions);

      // Find which session this belongs to
      let targetSession: SessionSummary | undefined;
      if (d.sessionId) {
        targetSession = next.get(d.sessionId);
      }
      if (!targetSession) {
        for (const s of next.values()) {
          if (s.status === 'Running' &&
              s.agents.some(a => a.agentName === d.agentName)) {
            targetSession = s;
            break;
          }
        }
      }

      if (targetSession) {
        const session = { ...targetSession };

        // Find or create agent
        let agentIdx = session.agents
          .findIndex(a => a.agentName === d.agentName);

        if (agentIdx < 0 && d.agentName) {
          session.agents = [...session.agents, {
            agentName: d.agentName,
            status: 'Executing',
            actions: [],
            completedCount: 0,
            totalCount: 0,
            progressPercent: 0,
          }];
          agentIdx = session.agents.length - 1;
        }

        if (agentIdx >= 0) {
          const agent = { ...session.agents[agentIdx] };

          // Find or create action
          const actionIdx = agent.actions
            .findIndex(a => a.tag === d.nodeTag || a.tag === d.actionTag);

          const actionStatus = d.status as ActionStatus;

          if (actionIdx < 0 && (d.nodeTag || d.actionTag)) {
            agent.actions = [...agent.actions, {
              tag: d.nodeTag || d.actionTag || '',
              actionType: d.actionType || '',
              agentName: d.agentName || '',
              command: d.command || '',
              status: actionStatus,
              exitCode: d.exitCode,
              errorMessage: d.errorMessage,
              duration: d.duration,
            }];
          } else if (actionIdx >= 0) {
            agent.actions = [...agent.actions];
            agent.actions[actionIdx] = {
              ...agent.actions[actionIdx],
              status: actionStatus,
              exitCode: d.exitCode,
              errorMessage: d.errorMessage,
              duration: d.duration,
            };
          }

          // Update agent counters
          agent.completedCount = agent.actions
            .filter(a => a.status === 'Success' || a.status === 'Failed').length;
          agent.totalCount = Math.max(agent.totalCount, agent.actions.length);
          agent.progressPercent = agent.totalCount > 0
            ? Math.round(agent.completedCount / agent.totalCount * 100)
            : 0;

          // Update agent status
          if (agent.actions.some(a => a.status === 'Failed')) {
            agent.status = 'Failed';
          } else if (agent.actions.some(a => a.status === 'Running')) {
            agent.status = 'Executing';
          } else if (agent.completedCount === agent.totalCount && agent.totalCount > 0) {
            agent.status = 'Success';
          }

          agent.currentAction = agent.actions
            .find(a => a.status === 'Running')?.command;

          session.agents = [...session.agents];
          session.agents[agentIdx] = agent;
        }

        // Update session counters
        session.completedActions = session.agents
          .reduce((sum, a) => sum + a.completedCount, 0);
        session.passedActions = session.agents
          .reduce((sum, a) =>
            sum + a.actions.filter(act => act.status === 'Success').length, 0);
        session.failedActions = session.agents
          .reduce((sum, a) =>
            sum + a.actions.filter(act => act.status === 'Failed').length, 0);
        session.totalActions = Math.max(
          session.totalActions,
          session.agents.reduce((sum, a) => sum + a.totalCount, 0));
        session.progressPercent = session.totalActions > 0
          ? Math.round(session.completedActions / session.totalActions * 100)
          : 0;

        next.set(session.sessionId, session);
      }

      return { ...state, sessions: next };
    }

    case 'LOG_ENTRY':
    case 'AGENT_OUTPUT': {
      const entry: DashboardLogEntry = action.type === 'LOG_ENTRY'
        ? action.data
        : {
            timestamp: action.data.timestamp || new Date().toLocaleTimeString(),
            sessionId: action.data.sessionId || '',
            agentName: action.data.agentName || '',
            category: action.data.kind || 'output',
            message: action.data.line || action.data.message || '',
            severity: action.data.kind === 'stderr' || action.data.severity === 'Error'
              ? 'Error' : 'Info',
          };

      let logs = [...state.logs, entry];
      if (logs.length > state.maxLogs) {
        logs = logs.slice(-state.maxLogs);
      }
      return { ...state, logs };
    }

    case 'SELECT_SESSION':
      return { ...state, selectedSessionId: action.sessionId };

    case 'SELECT_AGENT':
      return { ...state, selectedAgentName: action.agentName };

    case 'CLEAR_LOGS':
      return { ...state, logs: [] };

    default:
      return state;
  }
}

// ── Context ──
interface ExecutionDashboardContextValue {
  state: ExecutionDashboardState;
  dispatch: Dispatch<Action>;
  activeSessions: SessionSummary[];
  completedSessions: SessionSummary[];
  filteredLogs: DashboardLogEntry[];
  selectSession: (id: string | null) => void;
  selectAgent: (name: string | null) => void;
}

const ExecutionDashboardContext = createContext<ExecutionDashboardContextValue>(
  null as any);

export function useExecutionDashboard() {
  return useContext(ExecutionDashboardContext);
}

// ── Provider ──
export function ExecutionDashboardProvider({ children }: { children: ReactNode }) {
  const [state, dispatch] = useReducer(reducer, initialState);
  const connection = useConnectionStore((s: { connection: any }) => s.connection);

  // Load existing sessions on mount
  useEffect(() => {
    apiFetch<{ active: any[]; history: any[] }>('/api/execution/proxy/dashboard-sessions')
      .then(data => {
        const all = [
          ...(data.active || []),
          ...(data.history || []).slice(0, 20),
        ];
        dispatch({ type: 'SET_SESSIONS', sessions: all });

        // Join SignalR groups for active sessions
        for (const s of data.active || []) {
          joinSession(connection, s.sessionId);
        }
      })
      .catch(logCatch('useExecutionDashboard', 'fetchSessions'));
  }, [connection]);

  // Subscribe to SignalR events for dashboard-specific state
  useEffect(() => {
    if (!connection) return;

    const onStarted = (data: any) => {
      dispatch({ type: 'EXECUTION_STARTED', data });
      joinSession(connection, data.sessionId);
    };
    const onCompleted = (data: any) => {
      dispatch({ type: 'EXECUTION_COMPLETED', data });
    };
    const onProgress = (data: any) => {
      dispatch({ type: 'ACTION_PROGRESS', data });
    };
    const onOutput = (data: any) => {
      dispatch({ type: 'AGENT_OUTPUT', data });
    };
    const onLog = (data: any) => {
      dispatch({ type: 'LOG_ENTRY', data });
    };

    const onCancelled = (data: any) => {
      dispatch({ type: 'EXECUTION_COMPLETED', data: { ...data, state: 'Cancelled' } });
    };

    connection.on('ExecutionStarted', onStarted);
    connection.on('ExecutionCompleted', onCompleted);
    connection.on('ExecutionCancelled', onCancelled);
    connection.on('ActionProgress', onProgress);
    connection.on('AgentOutput', onOutput);
    connection.on('LogEntry', onLog);

    return () => {
      connection.off('ExecutionStarted', onStarted);
      connection.off('ExecutionCompleted', onCompleted);
      connection.off('ExecutionCancelled', onCancelled);
      connection.off('ActionProgress', onProgress);
      connection.off('AgentOutput', onOutput);
      connection.off('LogEntry', onLog);
    };
  }, [connection]);

  // Derived data
  const sessions: SessionSummary[] = Array.from(state.sessions.values());
  const activeSessions = sessions
    .filter((s: SessionSummary) => s.status === 'Running' || s.status === 'Queued')
    .sort((a: SessionSummary, b: SessionSummary) =>
      new Date(b.startedUtc).getTime() - new Date(a.startedUtc).getTime());

  const completedSessions = sessions
    .filter((s: SessionSummary) => s.status !== 'Running' && s.status !== 'Queued')
    .sort((a: SessionSummary, b: SessionSummary) =>
      new Date(b.startedUtc).getTime() - new Date(a.startedUtc).getTime())
    .slice(0, 20);

  // Filtered logs based on selection
  const filteredLogs = state.logs.filter((entry: DashboardLogEntry) => {
    if (state.selectedSessionId && entry.sessionId !== state.selectedSessionId) {
      return false;
    }
    if (state.selectedAgentName && entry.agentName !== state.selectedAgentName) {
      return false;
    }
    return true;
  });

  const selectSession = useCallback((id: string | null) => {
    dispatch({ type: 'SELECT_SESSION', sessionId: id });
    dispatch({ type: 'SELECT_AGENT', agentName: null });
  }, []);

  const selectAgent = useCallback((name: string | null) => {
    dispatch({ type: 'SELECT_AGENT', agentName: name });
  }, []);

  return (
    <ExecutionDashboardContext.Provider value={{
      state, dispatch,
      activeSessions, completedSessions, filteredLogs,
      selectSession, selectAgent,
    }}>
      {children}
    </ExecutionDashboardContext.Provider>
  );
}
