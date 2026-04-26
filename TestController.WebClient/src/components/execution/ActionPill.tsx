import type { ActionExecution } from '../../types/execution';

const pillClasses: Record<string, { classes: string; icon: string }> = {
  Pending: {
    classes: 'bg-bg border-bdr text-text-muted',
    icon: '\u25CB',
  },
  Running: {
    classes: 'bg-accent/10 border-accent/30 text-accent',
    icon: '\u25CF',
  },
  Success: {
    classes: 'bg-acc-green/10 border-acc-green/30 text-acc-green',
    icon: '\u2713',
  },
  Failed: {
    classes: 'bg-acc-red/10 border-acc-red/30 text-acc-red',
    icon: '\u2717',
  },
  Skipped: {
    classes: 'bg-bg-surface border-bdr text-text-muted',
    icon: '\u2212',
  },
};

interface Props {
  action: ActionExecution;
}

export function ActionPill({ action }: Props) {
  const style = pillClasses[action.status] || pillClasses.Pending;

  // Shorten command for display
  let label = action.tag || action.command || 'Action';
  if (label.length > 22) {
    label = label.substring(0, 20) + '...';
  }

  // Add progress if running
  if (action.status === 'Running' && action.progressPercent) {
    label += ` ${Math.round(action.progressPercent)}%`;
  }

  return (
    <span
      title={
        `${action.command || action.tag}\n` +
        `Status: ${action.status}\n` +
        (action.exitCode !== undefined ? `Exit: ${action.exitCode}\n` : '') +
        (action.errorMessage || '') +
        (action.duration ? `\nDuration: ${action.duration}` : '')
      }
      className={`inline-flex items-center gap-1 text-[11px] px-2 py-0.5 rounded border whitespace-nowrap max-w-[200px] overflow-hidden text-ellipsis cursor-default ${style.classes}`}
    >
      <span className="text-[10px] shrink-0">{style.icon}</span>
      {label}
    </span>
  );
}
