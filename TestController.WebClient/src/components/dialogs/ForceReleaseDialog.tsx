import { useState } from 'react';
import { AlertTriangle, Unlock } from 'lucide-react';
import { apiFetch } from '../../lib/api';
import type { PipelineLockDto } from '../../stores/lockStore';

interface ForceReleaseDialogProps {
  open: boolean;
  lock: PipelineLockDto;
  onClose: () => void;
}

export default function ForceReleaseDialog({ open, lock, onClose }: ForceReleaseDialogProps) {
  const [reason, setReason] = useState('');
  const [isAffirmed, setIsAffirmed] = useState(false);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const canSubmit = reason.length >= 10 && isAffirmed && !isSubmitting;

  const handleSubmit = async () => {
    if (!canSubmit) return;
    setIsSubmitting(true);
    setError(null);
    try {
      await apiFetch(`/api/locks/${encodeURIComponent(lock.pipelineId)}/force-release`, {
        method: 'POST',
        body: JSON.stringify({ reason }),
      });
      onClose();
    } catch (err: any) {
      setError(err.error || 'Force-release failed');
    } finally {
      setIsSubmitting(false);
    }
  };

  if (!open) return null;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center">
      <div className="absolute inset-0 bg-black/60 backdrop-blur-sm" onClick={onClose} />
      <div className="relative w-full max-w-md rounded-lg border border-bdr bg-bg-surface p-6 shadow-xl">
        {/* Header */}
        <div className="flex items-center gap-3 mb-4">
          <div className="flex items-center justify-center w-10 h-10 rounded-full bg-amber-900/30">
            <AlertTriangle size={20} className="text-amber-400" />
          </div>
          <div>
            <h2 className="text-sm font-semibold text-text-primary">Take over and revert</h2>
            <p className="text-xs text-text-secondary">This action cannot be undone</p>
          </div>
        </div>

        {/* Owner card */}
        <div className="rounded-md border border-bdr bg-bg-ribbon p-3 mb-4">
          <p className="text-xs text-text-primary font-medium">{lock.pipelineId}</p>
          <p className="text-[10px] text-text-muted mt-1">
            Current owner: {lock.ownerDisplayName} ({lock.ownerClientKind})
          </p>
        </div>

        {/* Side effects */}
        <div className="rounded-md bg-amber-900/10 border border-amber-900/30 p-3 mb-4">
          <p className="text-[10px] font-medium text-amber-400 mb-1.5">This action will:</p>
          <ul className="text-[10px] text-text-secondary space-y-0.5 list-disc list-inside">
            <li>Cancel {lock.ownerDisplayName}'s active session</li>
            <li>Revert any in-progress changes</li>
            <li>Notify {lock.ownerDisplayName} with your reason</li>
            <li>Record this action in the audit log</li>
          </ul>
        </div>

        {/* Reason textarea */}
        <div className="mb-3">
          <label className="block text-[10px] text-text-muted mb-1">
            Reason for taking over
          </label>
          <textarea
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            placeholder="Explain why you are taking over this pipeline..."
            className="w-full h-20 px-3 py-2 rounded border border-bdr bg-bg-ribbon text-xs text-text-primary placeholder:text-text-muted resize-none focus:outline-none focus:border-accent"
          />
          <p className={`text-[10px] mt-0.5 ${reason.length >= 10 ? 'text-green-400' : 'text-text-muted'}`}>
            {reason.length} / 10 minimum
          </p>
        </div>

        {/* Affirmation checkbox */}
        <label className="flex items-start gap-2 mb-4 cursor-pointer">
          <input
            type="checkbox"
            checked={isAffirmed}
            onChange={(e) => setIsAffirmed(e.target.checked)}
            className="mt-0.5 rounded border-bdr"
          />
          <span className="text-[10px] text-text-secondary">
            I confirm that {lock.ownerDisplayName}'s run will be cancelled
          </span>
        </label>

        {/* Error */}
        {error && (
          <p className="text-[10px] text-red-400 mb-3">{error}</p>
        )}

        {/* Actions */}
        <div className="flex items-center gap-2">
          <button
            onClick={onClose}
            className="px-3 py-1.5 rounded text-xs font-medium bg-white/5 text-text-secondary hover:bg-white/10 transition-colors"
          >
            Cancel
          </button>
          <button
            onClick={handleSubmit}
            disabled={!canSubmit}
            className={`px-3 py-1.5 rounded text-xs font-medium flex items-center gap-1.5 transition-colors ${
              canSubmit
                ? 'bg-amber-900/30 text-amber-400 hover:bg-amber-900/50 cursor-pointer'
                : 'bg-white/5 text-text-secondary opacity-45 cursor-not-allowed'
            }`}
          >
            <Unlock size={12} />
            Take over and revert
          </button>
        </div>
      </div>
    </div>
  );
}
