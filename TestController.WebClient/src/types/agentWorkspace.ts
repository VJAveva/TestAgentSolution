// Agent Workspace types — mirrors WebApi fleet/telemetry/details endpoints

export type WorkspaceMode = 'fleet' | 'monitor' | 'registry';

export interface FleetAgent {
  name: string;
  address: string;
  status: string;
  lastStatusDetail: string | null;
  lastCheckedUtc: string | null;
  isLocked: boolean;
  lockedBy: string | null;
  lockSource: string | null;
  lockedAtUtc: string | null;
  watchItemTag: string | null;
}

export interface FleetResponse {
  agents: FleetAgent[];
  lockVersion: number;
}

export interface AgentTelemetry {
  agentName: string;
  state: string;
  currentActivity: string;
  currentCommand: string;
  executionsCompleted: number;
  executionsFailed: number;
  cpuUsagePct: number;
  memoryUsedMb: number;
  memoryTotalMb: number;
  diskFreeGb: number;
  activeProcessCount: number;
  timestamp: string;
}

export interface AgentMetrics {
  cpuUsagePct: number;
  memoryUsedMb: number;
  memoryTotalMb: number;
  diskFreeGb: number;
  activeProcessCount: number;
}

export interface AgentSnapshot {
  agentName: string;
  state: string;
  currentActivity: string;
  currentExecutionId: string;
  currentCommand: string;
  executionStarted: string | null;
  agentStarted: string | null;
  executionsCompleted: number;
  executionsFailed: number;
  metrics: AgentMetrics | null;
}

export interface AgentLockInfo {
  sessionId: string;
  source: string;
  watchItemTag: string;
  lockedAtUtc: string;
}

export interface AgentDetails {
  name: string;
  address: string;
  status: string;
  lastStatusDetail: string | null;
  lastCheckedUtc: string | null;
  snapshot: AgentSnapshot | null;
  snapshotError: string | null;
  lock: AgentLockInfo | null;
}
