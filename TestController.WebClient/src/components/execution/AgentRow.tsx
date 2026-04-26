import { ActionPill } from './ActionPill';
import type { AgentExecution } from '../../types/execution';

const agentStatusClasses: Record<string, { color: string; text: string }> = {
  Idle:      { color: 'text-text-muted', text: 'Idle' },
  Executing: { color: 'text-accent', text: 'Executing' },
  Rebooting: { color: 'text-acc-amber', text: 'Rebooting' },
  Success:   { color: 'text-acc-green', text: 'Done' },
  Failed:    { color: 'text-acc-red', text: 'Failed' },
};

const agentProgressBg: Record<string, string> = {
  Idle:      'bg-text-muted',
  Executing: 'bg-accent',
  Rebooting: 'bg-acc-amber',
  Success:   'bg-acc-green',
  Failed:    'bg-acc-red',
};

interface Props {
  agent: AgentExecution;
  onClick: () => void;
}

export function AgentRow({ agent, onClick }: Props) {
  const status = agentStatusClasses[agent.status] || agentStatusClasses.Idle;
  const progressBg = agentProgressBg[agent.status] || 'bg-text-muted';

  return (
    <div className="flex items-stretch border-b border-bdr last:border-b-0">
      {/* Agent name column (fixed width) */}
      <div
        onClick={onClick}
        className="w-[120px] shrink-0 px-3 py-2 cursor-pointer border-r border-bdr bg-bg-surface flex flex-col justify-center"
      >
        <div className="font-mono text-xs font-medium text-text-primary">
          {agent.agentName}
        </div>
        <div className={`text-[10px] mt-0.5 ${status.color}`}>
          {status.text}
        </div>
        {/* Progress bar */}
        <div className="h-[3px] mt-1 bg-bg rounded-full overflow-hidden">
          <div
            className={`h-full rounded-full transition-all duration-500 ${progressBg}`}
            style={{ width: `${agent.progressPercent}%` }}
          />
        </div>
      </div>

      {/* Action chain (flexible width, wraps) */}
      <div className="flex-1 px-2.5 py-1.5 flex flex-wrap gap-[3px] items-center content-center">
        {agent.actions.map((action, i) => (
          <span key={action.tag || i} className="inline-flex items-center gap-[3px]">
            {i > 0 && (
              <span className="text-text-muted text-[9px]">&#8594;</span>
            )}
            <ActionPill action={action} />
          </span>
        ))}

        {agent.actions.length === 0 && (
          <span className="text-[11px] text-text-muted">
            Waiting for actions...
          </span>
        )}
      </div>
    </div>
  );
}
