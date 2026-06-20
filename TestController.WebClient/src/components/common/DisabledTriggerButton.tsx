import { useCan, useDisabledReason } from '../../hooks/useCapabilities';
import { useLockStore } from '../../stores/lockStore';
import { Lock } from 'lucide-react';

interface DisabledTriggerButtonProps {
  /** Original onClick handler (only called when permission is granted). */
  onTrigger?: () => void;
  label?: string;
  className?: string;
  /** Pipeline tag for resource-scoped permission check. */
  pipelineTag?: string;
  /** Optional extra disabled gate (e.g., pipeline config disabled). */
  forceDisabled?: boolean;
  /** Optional tooltip reason used when forceDisabled is true. */
  forceReason?: string;
}

/**
 * Trigger button that disables based on capability checks and lock state.
 * Tooltip explains WHY the button is disabled (Default mode, role, assignment, or lock).
 * Per Mockup 4 and Mockup 11 tooltip copy.
 */
export default function DisabledTriggerButton({
  onTrigger,
  label = 'Trigger',
  className = '',
  pipelineTag,
  forceDisabled = false,
  forceReason,
}: DisabledTriggerButtonProps) {
  const allowed = useCan('Pipeline_Trigger', pipelineTag);
  const reason = useDisabledReason('Pipeline_Trigger', pipelineTag);
  const lock = useLockStore((s) => pipelineTag ? s.locks[pipelineTag] : undefined);
  const hasActiveLock = useLockStore((s) => pipelineTag ? s.hasActiveLock(pipelineTag) : false);

  // Single-run gate takes priority — while a run is active NOBODY can trigger a
  // second one (owner and Administrator included). The path forward is Cancel.
  if (hasActiveLock && lock) {
    return (
      <div className="relative group inline-block">
        <button
          disabled
          className={`px-3 py-1.5 rounded text-xs font-medium bg-white/5 text-text-secondary cursor-not-allowed opacity-50 ${className}`}
        >
          {label}
        </button>
        {/* Tooltip */}
        <div className="absolute bottom-full left-1/2 -translate-x-1/2 mb-2 px-3 py-1.5 rounded bg-bg-ribbon border border-bdr text-xs text-text-secondary whitespace-nowrap opacity-0 group-hover:opacity-100 transition-opacity pointer-events-none z-50">
          Pipeline is running · Locked by {lock.ownerDisplayName} ({lock.ownerClientKind})
        </div>
      </div>
    );
  }

  if (!allowed) {
    return (
      <div className="relative group inline-block">
        <button
          disabled
          className={`inline-flex items-center gap-1 px-3 py-1.5 rounded text-xs font-medium bg-white/5 text-text-secondary cursor-not-allowed opacity-50 ${className}`}
        >
          <Lock size={10} />
          {label}
        </button>
        {/* Tooltip */}
        <div className="absolute bottom-full left-1/2 -translate-x-1/2 mb-2 px-3 py-1.5 rounded bg-bg-ribbon border border-bdr text-xs text-text-secondary whitespace-nowrap opacity-0 group-hover:opacity-100 transition-opacity pointer-events-none z-50">
          {reason}
        </div>
      </div>
    );
  }

  if (forceDisabled) {
    return (
      <div className="relative group inline-block">
        <button
          disabled
          className={`inline-flex items-center gap-1 px-3 py-1.5 rounded text-xs font-medium bg-white/5 text-text-secondary cursor-not-allowed opacity-50 ${className}`}
        >
          <Lock size={10} />
          {label}
        </button>
        {/* Tooltip */}
        <div className="absolute bottom-full left-1/2 -translate-x-1/2 mb-2 px-3 py-1.5 rounded bg-bg-ribbon border border-bdr text-xs text-text-secondary whitespace-nowrap opacity-0 group-hover:opacity-100 transition-opacity pointer-events-none z-50">
          {forceReason || 'Action is disabled'}
        </div>
      </div>
    );
  }

  return (
    <button
      onClick={onTrigger}
      className={`px-3 py-1.5 rounded text-xs font-medium bg-accent/20 text-accent hover:bg-accent/30 transition-colors ${className}`}
    >
      {label}
    </button>
  );
}
