/**
 * Central API endpoint type contracts (CLIENT-006).
 *
 * Maps every REST endpoint to its request/response types.
 * This file serves as the single source of truth for API
 * shape expectations between the WebClient and WebApi.
 *
 * Usage:
 *   import type { ApiEndpoints } from '../types/endpoints';
 *   type Resp = ApiEndpoints['/api/agents/fleet']['response'];
 */

import type {
  FleetResponse,
  AgentTelemetry,
  AgentDetails,
  AgentLockInfo,
} from './agentWorkspace';

import type {
  SessionSummary,
  DashboardLogEntry,
} from './execution';

import type {
  BuildSummary,
  BuildNode,
  BuildDetailResponse,
  TrendReport,
  ConsecutiveFailureAlert,
  SessionsResponse,
  AgentInfo,
  DiagnosticStep,
} from './api';

// ── Response-only endpoints (GET) ───────────────────────────────────

export interface AgentFleetEndpoint {
  response: FleetResponse;
}

export interface AgentTelemetryEndpoint {
  response: AgentTelemetry;
}

export interface AgentDetailsEndpoint {
  response: AgentDetails;
}

export interface ExecutionLocksEndpoint {
  response: { locks: AgentLockInfo[] };
}

export interface DashboardSessionsEndpoint {
  response: { active: SessionSummary[]; history: SessionSummary[] };
}

export interface DashboardLogsEndpoint {
  response: { logs: DashboardLogEntry[]; sessionId: string };
}

export interface BuildsListEndpoint {
  response: BuildSummary[];
}

export interface BuildDetailEndpoint {
  response: BuildNode;
}

export interface BuildFullDetailEndpoint {
  response: BuildDetailResponse;
}

export interface TrendEndpoint {
  response: TrendReport;
}

export interface AlertsEndpoint {
  response: ConsecutiveFailureAlert[];
}

export interface SessionsEndpoint {
  response: SessionsResponse;
}

export interface AgentsEndpoint {
  response: AgentInfo[];
}

export interface DiagnosticsEndpoint {
  response: { steps: DiagnosticStep[]; agentName: string };
}

export interface HealthEndpoint {
  response: { status: string; timestamp: string };
}

// ── Mutation endpoints (POST/PUT/DELETE) ─────────────────────────────

export interface ForceReleaseEndpoint {
  request: { reason: string };
  response: { success: boolean; message?: string };
}

export interface ForceReleaseAllEndpoint {
  request: { reason: string };
  response: { success: boolean; released: string[] };
}

export interface ExecuteEndpoint {
  request: { watchItemTag: string; eventType?: string; userId?: string };
  response: { sessionId: string };
}

export interface CancelExecutionEndpoint {
  request: { sessionId: string };
  response: { success: boolean };
}

// ── Master route map ────────────────────────────────────────────────

export interface ApiEndpoints {
  '/api/agents/fleet': AgentFleetEndpoint;
  '/api/agents/:name/telemetry': AgentTelemetryEndpoint;
  '/api/agents/:name/details': AgentDetailsEndpoint;
  '/api/agents': AgentsEndpoint;
  '/api/agents/:name/diagnostics': DiagnosticsEndpoint;
  '/api/execution/locks': ExecutionLocksEndpoint;
  '/api/execution/dashboard-sessions': DashboardSessionsEndpoint;
  '/api/execution/:sessionId/recent-logs': DashboardLogsEndpoint;
  '/api/execution/force-release/:agentName': ForceReleaseEndpoint;
  '/api/execution/force-release-all': ForceReleaseAllEndpoint;
  '/api/execution/execute': ExecuteEndpoint;
  '/api/execution/cancel': CancelExecutionEndpoint;
  '/api/execution/sessions': SessionsEndpoint;
  '/api/results/builds': BuildsListEndpoint;
  '/api/results/builds/:buildNumber': BuildDetailEndpoint;
  '/api/results/builds/:buildNumber/detail': BuildFullDetailEndpoint;
  '/api/results/trends': TrendEndpoint;
  '/api/results/alerts': AlertsEndpoint;
  '/api/health': HealthEndpoint;
}
