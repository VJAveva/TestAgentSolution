export interface SessionSummary {
  sessionId: string;
  watchItemTag: string;
  userId: string;
  owner?: RunOwner;
  source: string;
  status: SessionStatus;
  startedUtc: string;
  elapsed: string;
  agents: AgentExecution[];
  totalActions: number;
  completedActions: number;
  passedActions: number;
  failedActions: number;
  progressPercent: number;
  lockedAgents: string[];
  buildNumber?: string;
}

/// Attribution for who triggered a run (display only — not an authorization input).
export interface RunOwner {
  userId: string;
  displayName: string;
  role: string;
}

export type SessionStatus =
  | 'Running'
  | 'Success'
  | 'Failed'
  | 'PartialFailure'
  | 'Cancelled'
  | 'Queued';

export interface AgentExecution {
  agentName: string;
  status: AgentStatus;
  actions: ActionExecution[];
  completedCount: number;
  totalCount: number;
  progressPercent: number;
  currentAction?: string;
  lastOutput?: string;
}

export type AgentStatus =
  | 'Idle'
  | 'Executing'
  | 'Rebooting'
  | 'Success'
  | 'Failed';

export interface ActionExecution {
  tag: string;
  actionType: string;
  agentName: string;
  command: string;
  status: ActionStatus;
  exitCode?: number;
  errorMessage?: string;
  duration?: string;
  progressPercent?: number;
  startedUtc?: string;
  durationSeconds?: number;
}

export type ActionStatus =
  | 'Pending'
  | 'Running'
  | 'Success'
  | 'Failed'
  | 'Skipped';

export interface DashboardLogEntry {
  timestamp: string;
  sessionId: string;
  agentName: string;
  category: string;
  message: string;
  severity: 'Info' | 'Warning' | 'Error' | 'Success';
}
