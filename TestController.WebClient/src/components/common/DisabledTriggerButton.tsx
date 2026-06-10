import { useSystemModeStore } from '../../stores/systemModeStore';

interface DisabledTriggerButtonProps {
  /** Original onClick handler (only called when mode is secured). */
  onTrigger?: () => void;
  label?: string;
  className?: string;
}

/**
 * Trigger button that is visibly disabled (greyed out) in Default mode.
 * The button renders but is non-interactive, with a tooltip explaining why.
 * Per design: button is disabled, NOT hidden — maintains UI discoverability.
 */
export default function DisabledTriggerButton({ onTrigger, label = 'Trigger', className = '' }: DisabledTriggerButtonProps) {
  const isDefault = useSystemModeStore((s) => s.isDefault);

  if (isDefault) {
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
          Trigger is unavailable in Default mode. Switch to Secured mode to enable pipeline triggering from the Web Client.
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
