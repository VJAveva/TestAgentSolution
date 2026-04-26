import React, { useState } from 'react';
import { useExecutionDashboard } from '../../hooks/useExecutionDashboard';
import { AgentRow } from './AgentRow';
import { apiFetch } from '../../lib/api';
import type { SessionSummary } from '../../types/execution';

interface Props {
  session: SessionSummary;
  isSelected: boolean;
  onSelect: () => void;
  collapsed?: boolean;
}

const statusClasses: Record<string, { badge: string; label: string }> = {
  Running:   { badge: 'bg-accent/10 text-accent', label: 'Running' },
  Success:   { badge: 'bg-acc-green/10 text-acc-green', label: 'Completed' },
  Failed:    { badge: 'bg-acc-red/10 text-acc-red', label: 'Failed' },
  Cancelled: { badge: 'bg-acc-amber/10 text-acc-amber', label: 'Cancelled' },
  Queued:    { badge: 'bg-bg-surface text-text-muted', label: 'Queued' },
};

export function SessionCard({
  session, isSelected, onSelect, collapsed = false
}: Props) {
  const [isExpanded, setIsExpanded] = useState(!collapsed);
  const { selectAgent } = useExecutionDashboard();
  const style = statusClasses[session.status] || statusClasses.Queued;

  const handleCancel = async (e: React.MouseEvent) => {
    e.stopPropagation();
    if (!confirm(`Cancel session "${session.watchItemTag}"?`)) return;
    try {
      await apiFetch(
        `/api/execution/${session.sessionId}/cancel`,
        { method: 'POST' });
    } catch { /* handled by apiFetch logging */ }
  };

  return (
    <div className={`bg-bg-card border rounded-lg mb-2 overflow-hidden ${
      isSelected ? 'border-accent border-2' : 'border-bdr'
    }`}>
      {/* Session header */}
      <div
        onClick={() => { setIsExpanded(!isExpanded); onSelect(); }}
        className="flex items-center justify-between px-3.5 py-2.5 cursor-pointer gap-2"
      >
        <div className="flex items-center gap-2 text-sm font-medium">
          <span
            className="text-[10px] text-text-muted transition-transform duration-200"
            style={{ transform: isExpanded ? 'rotate(90deg)' : 'rotate(0deg)' }}
          >
            &#9654;
          </span>

          {/* Status badge */}
          <span className={`text-[10px] font-medium px-2 py-0.5 rounded ${style.badge}`}>
            {style.label}
          </span>

          {/* Pipeline name */}
          <span className="text-text-primary">{session.watchItemTag}</span>

          {/* Build number */}
          {session.buildNumber && (
            <span className="text-[11px] font-mono text-text-muted">
              {session.buildNumber}
            </span>
          )}
        </div>

        <div className="flex items-center gap-3 text-[11px] text-text-secondary">
          {/* User badge */}
          <span className="font-mono text-[9px] px-1.5 py-0.5 rounded bg-bg-surface">
            {session.userId}
          </span>

          <span>{session.agents.length} agents</span>
          <span className="font-mono">{session.elapsed}</span>
          <span className="font-medium">{session.progressPercent}%</span>

          {session.status === 'Running' && (
            <button
              onClick={handleCancel}
              className="text-[10px] px-2 py-0.5 text-acc-red bg-acc-red/10 border border-acc-red/30 rounded cursor-pointer hover:bg-acc-red/20"
            >
              Cancel
            </button>
          )}
        </div>
      </div>

      {/* Agent rows (expanded) */}
      {isExpanded && (
        <div className="border-t border-bdr">
          {session.agents.map(agent => (
            <AgentRow
              key={agent.agentName}
              agent={agent}
              onClick={() => selectAgent(agent.agentName)}
            />
          ))}

          {session.agents.length === 0 && (
            <div className="p-4 text-center text-xs text-text-muted">
              Waiting for agent assignment...
            </div>
          )}
        </div>
      )}
    </div>
  );
}
