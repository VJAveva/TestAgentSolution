import { useExecutionStore } from '../../stores/executionStore';
import { useExecution } from '../../hooks/useExecution';
import { PlayCircle, XCircle } from 'lucide-react';

export default function SessionList() {
  const { isExecuting, activeCount } = useExecutionStore();
  const { triggerAll, cancelAll } = useExecution();

  return (
    <div className="p-3 space-y-3">
      <div className="bg-bg-card rounded-lg p-3 space-y-2">
        <div className="flex items-center gap-2">
          <span className={`w-2 h-2 rounded-full ${isExecuting ? 'bg-accent animate-pulse' : 'bg-text-muted'}`} />
          <span className="text-xs text-text-primary font-medium">
            {isExecuting ? `${activeCount} execution(s) active` : 'Idle'}
          </span>
        </div>

        <div className="flex gap-1">
          <button
            className="flex items-center gap-1 px-2 py-1 rounded text-xs bg-accent/15 text-accent hover:bg-accent/25"
            onClick={() => triggerAll()}
          >
            <PlayCircle size={12} /> Trigger All
          </button>
          <button
            className="flex items-center gap-1 px-2 py-1 rounded text-xs bg-acc-red/15 text-acc-red hover:bg-acc-red/25"
            onClick={() => cancelAll()}
          >
            <XCircle size={12} /> Cancel
          </button>
        </div>
      </div>

      <div className="text-xs text-text-muted">
        Session history is shown in the live log panel. Use the Trigger / Cancel buttons above to control execution.
      </div>
    </div>
  );
}
