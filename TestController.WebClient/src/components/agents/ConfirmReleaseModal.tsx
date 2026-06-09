import { useState, useRef, useEffect } from 'react';
import { AlertTriangle } from 'lucide-react';

interface ConfirmReleaseModalProps {
  /** Whether the modal is visible */
  open: boolean;
  /** Title shown in the modal header */
  title: string;
  /** Descriptive message explaining the action */
  message: string;
  /** Whether to require a reason input before confirming */
  requireReason?: boolean;
  /** Placeholder for the reason input */
  reasonPlaceholder?: string;
  /** Callback on confirm — receives the reason (empty string if not required) */
  onConfirm: (reason: string) => void;
  /** Callback on cancel */
  onCancel: () => void;
}

/**
 * Modal dialog for confirming destructive actions (force release).
 * Optionally requires the user to enter a reason, which is then sent
 * to the server for audit logging.
 *
 * Replaces the bare `confirm()` calls (CLIENT-003 hardening).
 */
export default function ConfirmReleaseModal({
  open,
  title,
  message,
  requireReason = true,
  reasonPlaceholder = 'Reason for release (e.g., stuck pipeline, test rerun)',
  onConfirm,
  onCancel,
}: ConfirmReleaseModalProps) {
  const [reason, setReason] = useState('');
  const inputRef = useRef<HTMLInputElement>(null);

  // Focus input when modal opens
  useEffect(() => {
    if (open) {
      setReason('');
      setTimeout(() => inputRef.current?.focus(), 50);
    }
  }, [open]);

  // Close on Escape
  useEffect(() => {
    if (!open) return;
    const handler = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onCancel();
    };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, [open, onCancel]);

  if (!open) return null;

  const canConfirm = !requireReason || reason.trim().length > 0;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center">
      {/* Backdrop */}
      <div
        className="absolute inset-0 bg-black/60 backdrop-blur-sm"
        onClick={onCancel}
      />

      {/* Modal */}
      <div className="relative bg-bg-panel border border-bdr rounded-lg shadow-2xl w-full max-w-md mx-4 p-6">
        {/* Header */}
        <div className="flex items-center gap-3 mb-4">
          <div className="flex-shrink-0 p-2 rounded-full bg-acc-red/10">
            <AlertTriangle size={20} className="text-acc-red" />
          </div>
          <h3 className="text-sm font-bold text-text-primary">{title}</h3>
        </div>

        {/* Message */}
        <p className="text-xs text-text-muted mb-4 leading-relaxed">{message}</p>

        {/* Reason input */}
        {requireReason && (
          <div className="mb-4">
            <label className="block text-[10px] text-text-muted uppercase tracking-wider mb-1.5">
              Reason (required)
            </label>
            <input
              ref={inputRef}
              type="text"
              value={reason}
              onChange={e => setReason(e.target.value)}
              placeholder={reasonPlaceholder}
              className="w-full px-3 py-2 text-xs bg-black/30 border border-bdr rounded
                         text-text-primary placeholder:text-text-muted/50
                         focus:outline-none focus:border-accent"
              onKeyDown={e => {
                if (e.key === 'Enter' && canConfirm) onConfirm(reason.trim());
              }}
            />
          </div>
        )}

        {/* Actions */}
        <div className="flex justify-end gap-2">
          <button
            className="px-4 py-1.5 text-xs text-text-muted hover:text-text-primary rounded
                       hover:bg-white/5 transition-colors"
            onClick={onCancel}
          >
            Cancel
          </button>
          <button
            className="px-4 py-1.5 text-xs font-semibold rounded transition-colors
                       bg-acc-red/20 text-acc-red hover:bg-acc-red/30
                       disabled:opacity-40 disabled:cursor-not-allowed"
            disabled={!canConfirm}
            onClick={() => onConfirm(reason.trim())}
          >
            Confirm Release
          </button>
        </div>
      </div>
    </div>
  );
}
