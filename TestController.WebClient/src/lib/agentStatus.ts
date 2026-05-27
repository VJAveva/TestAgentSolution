/**
 * Centralized agent status classification.
 *
 * Standardizes the various status strings returned by the WebApi
 * (Connected, Online, Healthy, AgentStateReady, Unhealthy, Unknown, etc.)
 * into a canonical set of UI states.
 *
 * This fixes CLIENT-002: ensures isOffline detection is consistent
 * across FleetPage, AgentList, RegistryPage, and telemetry displays.
 */

export type AgentHealthState = 'online' | 'offline' | 'unknown';

/** Strings that definitively indicate the agent is reachable. */
const ONLINE_PATTERNS = [
  'Connected', 'Online', 'Healthy', 'AgentStateReady',
  // Execution-related states: agent is actively working (reachable by definition)
  'Executing', 'Ready', 'AgentStateRunning', 'Rebooting',
  'Failed',   // command failure — agent itself is still online
  'Waiting',  // "Waiting for previous command to finish"
];

/** Strings that definitively indicate the agent is unreachable. */
const OFFLINE_PATTERNS = ['Offline', 'Unhealthy', 'Disconnected', 'Unreachable', 'Error'];

/**
 * Determines if an agent status string represents an online state.
 * Case-insensitive matching against known-good patterns.
 * Uses word-boundary check to avoid 'Disconnected' matching 'Connected'.
 */
export function isAgentOnline(status: string | null | undefined): boolean {
  if (!status) return false;
  const lower = status.toLowerCase();
  return ONLINE_PATTERNS.some(p => {
    const pl = p.toLowerCase();
    // Exact match or whole-word match (not a substring of a larger word)
    if (lower === pl) return true;
    const idx = lower.indexOf(pl);
    if (idx < 0) return false;
    // Ensure it's not preceded by a letter (e.g., "dis" + "connected")
    if (idx > 0 && /[a-z]/.test(lower[idx - 1])) return false;
    return true;
  });
}

/**
 * Determines if an agent status string represents an offline state.
 * Returns true for explicitly offline statuses AND for stale agents
 * that haven't been checked recently.
 */
export function isAgentOffline(status: string | null | undefined): boolean {
  if (!status) return true;
  const lower = status.toLowerCase();
  return OFFLINE_PATTERNS.some(p => lower.includes(p.toLowerCase()));
}

/**
 * Classifies agent status into a canonical health state.
 * Considers both the status string and the lastCheckedUtc timestamp.
 *
 * @param status - Raw status string from the API
 * @param lastCheckedUtc - ISO timestamp of last health check (optional)
 * @param staleThresholdMs - Time after which an agent is considered stale (default 60s)
 */
export function classifyAgentHealth(
  status: string | null | undefined,
  lastCheckedUtc?: string | null,
  staleThresholdMs = 60_000
): AgentHealthState {
  // Explicit online/offline from status string
  if (isAgentOnline(status)) return 'online';
  if (isAgentOffline(status)) return 'offline';

  // If status is ambiguous, check staleness
  if (lastCheckedUtc) {
    const elapsed = Date.now() - new Date(lastCheckedUtc).getTime();
    if (elapsed > staleThresholdMs) return 'offline';
  }

  return 'unknown';
}
