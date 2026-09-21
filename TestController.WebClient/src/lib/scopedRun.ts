import type { SessionInfo } from '../types/api';

/** Prefixes the executor stamps on a scoped run's EventType. Mirrors ExecutionSession.IsFullPipelineRun. */
const SCOPED_PREFIXES = ['Action:', 'Group:', 'Template:'];

/**
 * Label for a node-run session, or null when the session is a full pipeline run.
 *
 * Prefers the server's `isFullPipelineRun`; the prefix check is only a fallback for payloads
 * from an older host that does not send it yet.
 */
export function scopedRunLabel(session: Pick<SessionInfo, 'eventType' | 'isFullPipelineRun'>): string | null {
  const eventType = session.eventType ?? '';

  const scoped = session.isFullPipelineRun === undefined
    ? SCOPED_PREFIXES.some(p => eventType.toLowerCase().startsWith(p.toLowerCase()))
    : !session.isFullPipelineRun;

  if (!scoped) return null;

  const separator = eventType.indexOf(':');
  const name = separator >= 0 ? eventType.slice(separator + 1).trim() : eventType.trim();
  return `Node: ${name || eventType}`;
}
