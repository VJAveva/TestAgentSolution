import type { NodeStatus } from '../types/api';

/**
 * Maps a server-sent status onto a node status.
 *
 * An unrecognised status must NEVER fall back to 'Running' or 'Idle': the last message a node sends
 * is what the tree shows forever, so a non-terminal fallback leaves it pulsing with no pass or fail,
 * and 'Idle' hides the node entirely. Anything unknown is therefore treated as a failure.
 */
export function toNodeStatus(status: string | undefined): NodeStatus {
  switch (status) {
    case 'Running': return 'Running';
    case 'Success': return 'Success';
    case 'Failed': return 'Failed';
    case 'Skipped': return 'Skipped';
    case 'Cancelled': return 'Cancelled';
    case 'Pending': return 'Idle';
    default: return 'Failed';
  }
}

/** True once a node has reached a state it will not leave on its own. */
export function isTerminalNodeStatus(status: NodeStatus): boolean {
  return status === 'Success' || status === 'Failed'
    || status === 'Skipped' || status === 'Cancelled';
}
