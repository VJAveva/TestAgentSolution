import { useState, useEffect, useCallback } from 'react';
import { apiFetch } from '../../lib/api';
import { logCatch } from '../../lib/logger';
import ConfirmReleaseModal from './ConfirmReleaseModal';

interface AgentLockInfo {
  agentName: string;
  sessionId: string;
  watchItemTag: string;
  userId: string;
  source: string;
  lockedAtUtc: string;
  duration: string;
}

/**
 * Displays real-time agent lock status. Listens to AgentLocksChanged
 * events via a custom DOM event dispatched by useSignalR.
 * Force release actions now require a confirmation modal with a reason
 * (CLIENT-003 hardening).
 */
export default function AgentLockPanel() {
  const [locks, setLocks] = useState<AgentLockInfo[]>([]);
  const [releasing, setReleasing] = useState<string | null>(null);
  // Confirmation modal state
  const [confirmTarget, setConfirmTarget] = useState<string | 'all' | null>(null);

  const fetchLocks = useCallback(() => {
    apiFetch<{ locks: AgentLockInfo[] }>('/api/execution/locks')
      .then(data => setLocks(data.locks || []))
      .catch(logCatch('AgentLockPanel', 'fetchLocks'));
  }, []);

  // Load initial lock state
  useEffect(() => { fetchLocks(); }, [fetchLocks]);

  // Subscribe to real-time lock changes
  useEffect(() => {
    const handler = (e: Event) => {
      const detail = (e as CustomEvent).detail;
      setLocks(detail?.locks || []);
    };
    window.addEventListener('agent-locks-changed', handler);
    return () => window.removeEventListener('agent-locks-changed', handler);
  }, []);

  const handleForceRelease = async (agentName: string, reason: string) => {
    setReleasing(agentName);
    try {
      await apiFetch(`/api/execution/force-release/${encodeURIComponent(agentName)}`, {
        method: 'POST',
        body: JSON.stringify({ reason }),
      });
      fetchLocks();
    } catch (err) {
      logCatch('AgentLockPanel', 'forceRelease')(err);
    } finally {
      setReleasing(null);
    }
  };

  const handleForceReleaseAll = async (reason: string) => {
    try {
      await apiFetch('/api/execution/force-release-all', {
        method: 'POST',
        body: JSON.stringify({ reason }),
      });
      fetchLocks();
    } catch (err) {
      logCatch('AgentLockPanel', 'forceReleaseAll')(err);
    }
  };

  const handleConfirm = (reason: string) => {
    if (confirmTarget === 'all') {
      handleForceReleaseAll(reason);
    } else if (confirmTarget) {
      handleForceRelease(confirmTarget, reason);
    }
    setConfirmTarget(null);
  };

  const modalTitle = confirmTarget === 'all'
    ? `Force Release All (${locks.length} agents)`
    : `Force Release — ${confirmTarget}`;
  const modalMessage = confirmTarget === 'all'
    ? `This will release ALL ${locks.length} agent locks. Running pipelines will NOT be cancelled. Please provide a reason for audit.`
    : `This removes the lock on '${confirmTarget}' but does NOT cancel the running pipeline. Please provide a reason for audit.`;

  if (locks.length === 0) {
    return (
      <div className="p-3 text-xs text-text-muted">
        No agent locks active. All agents are free.
      </div>
    );
  }

  return (
    <div className="space-y-2 p-3">
      <ConfirmReleaseModal
        open={confirmTarget !== null}
        title={modalTitle}
        message={modalMessage}
        requireReason={true}
        onConfirm={handleConfirm}
        onCancel={() => setConfirmTarget(null)}
      />

      <div className="flex items-center justify-between mb-2">
        <h3 className="text-xs font-bold text-text-secondary uppercase tracking-wider">
          Agent Locks
        </h3>
        <div className="flex items-center gap-2">
          <span className="text-[10px] text-text-muted">
            {locks.length} locked
          </span>
          {locks.length > 1 && (
            <button
              className="px-2 py-0.5 bg-red-900/30 hover:bg-red-800/50 text-red-300 text-[10px] rounded-full"
              onClick={() => setConfirmTarget('all')}
            >
              Release All
            </button>
          )}
        </div>
      </div>

      {locks.map(lock => (
        <div
          key={lock.agentName}
          className="bg-amber-900/10 border border-amber-800/40 rounded-lg p-3"
        >
          <div className="flex items-center justify-between mb-1">
            <span className="text-sm font-semibold text-text-primary">
              {lock.agentName}
            </span>
            <div className="flex items-center gap-2">
              <button
                className="px-2 py-0.5 bg-red-900/40 hover:bg-red-800/60 text-red-300 text-[10px] font-bold rounded-full uppercase disabled:opacity-40"
                onClick={() => setConfirmTarget(lock.agentName)}
                disabled={releasing === lock.agentName}
              >
                {releasing === lock.agentName ? 'Releasing…' : 'Force Release'}
              </button>
              <span className="px-2 py-0.5 bg-amber-900/30 text-amber-400 text-[10px] font-bold rounded-full uppercase">
                Locked
              </span>
            </div>
          </div>
          <div className="text-xs text-text-muted space-y-0.5 mt-2">
            <div className="flex justify-between">
              <span>Pipeline:</span>
              <span className="font-semibold text-amber-300">{lock.watchItemTag}</span>
            </div>
            <div className="flex justify-between">
              <span>User:</span>
              <span>{lock.userId}</span>
            </div>
            <div className="flex justify-between">
              <span>Source:</span>
              <span>{lock.source}</span>
            </div>
            <div className="flex justify-between">
              <span>Duration:</span>
              <span className="font-mono">{lock.duration}</span>
            </div>
            <div className="flex justify-between">
              <span>Session:</span>
              <span className="font-mono text-[10px]">{lock.sessionId}</span>
            </div>
          </div>
        </div>
      ))}
    </div>
  );
}
