import { useState, useEffect, useRef } from 'react';
import { Lock, X, LayoutDashboard } from 'lucide-react';
import { useCan } from '../../hooks/useCapabilities';
import type { PipelineLockDto } from '../../stores/lockStore';
import ForceReleaseDialog from './ForceReleaseDialog';

interface LockConflictModalProps {
  open: boolean;
  lock: PipelineLockDto;
  onClose: () => void;
}

function formatCountdown(expiresUtc: string): string {
  const remaining = Math.max(0, Math.floor((new Date(expiresUtc).getTime() - Date.now()) / 1000));
  if (remaining === 0) return 'expired';
  const m = Math.floor(remaining / 60);
  const s = remaining % 60;
  return `${m}m ${s.toString().padStart(2, '0')}s`;
}

function getInitials(name: string): string {
  return name.charAt(0).toUpperCase();
}

export default function LockConflictModal({ open, lock, onClose }: LockConflictModalProps) {
  const canForceRelease = useCan('Pipeline_ForceRelease');
  const [countdown, setCountdown] = useState('');
  const [showForceRelease, setShowForceRelease] = useState(false);
  const intervalRef = useRef<ReturnType<typeof setInterval> | null>(null);

  useEffect(() => {
    if (open && lock) {
      setCountdown(formatCountdown(lock.expiresUtc));
      intervalRef.current = setInterval(() => {
        setCountdown(formatCountdown(lock.expiresUtc));
      }, 1000);
    }
    return () => {
      if (intervalRef.current) {
        clearInterval(intervalRef.current);
        intervalRef.current = null;
      }
    };
  }, [open, lock]);

  if (!open) return null;

  if (showForceRelease) {
    return (
      <ForceReleaseDialog
        open={true}
        lock={lock}
        onClose={() => {
          setShowForceRelease(false);
          onClose();
        }}
      />
    );
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center">
      <div className="absolute inset-0 bg-black/60 backdrop-blur-sm" onClick={onClose} />
      <div className="relative w-full max-w-md rounded-lg border border-bdr bg-bg-surface p-6 shadow-xl">
        {/* Header */}
        <div className="flex items-center gap-3 mb-4">
          <div className="flex items-center justify-center w-10 h-10 rounded-full bg-amber-900/30">
            <Lock size={20} className="text-amber-400" />
          </div>
          <div>
            <h2 className="text-sm font-semibold text-text-primary">Pipeline Locked</h2>
            <p className="text-xs text-text-secondary">
              Currently being run by {lock.ownerDisplayName} from the {lock.ownerClientKind} client
            </p>
          </div>
          <button onClick={onClose} className="ml-auto p-1 hover:bg-white/10 rounded">
            <X size={16} className="text-text-muted" />
          </button>
        </div>

        {/* Owner card */}
        <div className="rounded-md border border-bdr bg-bg-ribbon p-4 mb-4">
          <div className="flex items-center gap-3">
            <div className="flex items-center justify-center w-9 h-9 rounded-full bg-accent/20 text-accent text-sm font-bold">
              {getInitials(lock.ownerDisplayName)}
            </div>
            <div className="flex-1">
              <p className="text-xs font-medium text-text-primary">{lock.ownerDisplayName}</p>
              <div className="flex items-center gap-2 mt-0.5">
                <span className="px-1.5 py-0.5 text-[9px] rounded bg-white/10 text-text-secondary font-medium">
                  {lock.ownerClientKind}
                </span>
                <span className="text-[10px] text-text-muted">
                  Started {new Date(lock.acquiredUtc).toLocaleTimeString()}
                </span>
              </div>
            </div>
          </div>
          <div className="mt-3 pt-3 border-t border-bdr flex items-center justify-between">
            <span className="text-[10px] text-text-muted">Lock expires in</span>
            <span className="text-xs font-mono text-amber-400">{countdown}</span>
          </div>
        </div>

        {/* Actions */}
        <div className="flex items-center gap-2">
          <button
            onClick={onClose}
            className="px-3 py-1.5 rounded text-xs font-medium bg-white/5 text-text-secondary hover:bg-white/10 transition-colors"
          >
            Close
          </button>
          <button
            onClick={onClose}
            className="px-3 py-1.5 rounded text-xs font-medium bg-accent/20 text-accent hover:bg-accent/30 transition-colors flex items-center gap-1.5"
          >
            <LayoutDashboard size={12} />
            View dashboard
          </button>
        </div>

        {/* Force-release section — Senior Manager only */}
        {canForceRelease && (
          <div className="mt-4 pt-4 border-t border-bdr bg-amber-900/10 -mx-6 -mb-6 px-6 pb-6 rounded-b-lg">
            <p className="text-[10px] text-text-muted mb-2">
              You have permission to take over this pipeline and revert the current run.
            </p>
            <button
              onClick={() => setShowForceRelease(true)}
              className="px-3 py-1.5 rounded text-xs font-medium bg-amber-900/30 text-amber-400 hover:bg-amber-900/50 transition-colors"
            >
              Take over and revert…
            </button>
          </div>
        )}
      </div>
    </div>
  );
}
