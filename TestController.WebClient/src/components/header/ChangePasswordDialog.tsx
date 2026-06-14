import { useState, useRef, useEffect, FormEvent } from 'react';
import { useAuthStore } from '../../stores/authStore';

/**
 * Self-service change-password modal dialog.
 * Reuses authStore.changePassword (POST /api/auth/change-password).
 * On success: shows confirmation then logs the user out (backend revokes session).
 */
export default function ChangePasswordDialog({ onClose }: { onClose: () => void }) {
  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [validationError, setValidationError] = useState('');
  const [success, setSuccess] = useState(false);
  const { changePassword, logout, isLoading, error, clearError } = useAuthStore();
  const dialogRef = useRef<HTMLDivElement>(null);

  // Close on Escape
  useEffect(() => {
    const handler = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose(); };
    document.addEventListener('keydown', handler);
    return () => document.removeEventListener('keydown', handler);
  }, [onClose]);

  // Close on backdrop click
  const handleBackdrop = (e: React.MouseEvent) => {
    if (e.target === e.currentTarget) onClose();
  };

  const validate = (): boolean => {
    if (newPassword.length < 12) {
      setValidationError('Password must be at least 12 characters');
      return false;
    }
    if (!/[a-z]/.test(newPassword)) {
      setValidationError('Password must contain a lowercase letter');
      return false;
    }
    if (!/[A-Z]/.test(newPassword)) {
      setValidationError('Password must contain an uppercase letter');
      return false;
    }
    if (!/\d/.test(newPassword)) {
      setValidationError('Password must contain a digit');
      return false;
    }
    if (newPassword !== confirmPassword) {
      setValidationError('Passwords do not match');
      return false;
    }
    setValidationError('');
    return true;
  };

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    if (!validate()) return;
    const ok = await changePassword(currentPassword, newPassword);
    if (ok) {
      setSuccess(true);
      // Backend revokes all sessions; log out after a brief confirmation
      setTimeout(() => { logout(); }, 2000);
    }
  };

  const displayError = validationError || error;

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/50"
      onClick={handleBackdrop}
    >
      <div ref={dialogRef} className="w-full max-w-sm p-6 rounded-lg border border-border-default bg-bg-secondary shadow-xl">
        {success ? (
          <div className="text-center py-4">
            <div className="w-10 h-10 rounded-full bg-green-400/20 flex items-center justify-center mx-auto mb-3">
              <svg className="w-5 h-5 text-green-400" fill="currentColor" viewBox="0 0 20 20">
                <path fillRule="evenodd" d="M16.707 5.293a1 1 0 010 1.414l-8 8a1 1 0 01-1.414 0l-4-4a1 1 0 011.414-1.414L8 12.586l7.293-7.293a1 1 0 011.414 0z" clipRule="evenodd" />
              </svg>
            </div>
            <p className="text-sm text-text-primary font-medium">Password changed successfully</p>
            <p className="text-xs text-text-secondary mt-1">You will be redirected to sign in…</p>
          </div>
        ) : (
          <>
            <div className="flex items-center justify-between mb-4">
              <h2 className="text-sm font-semibold text-text-primary">Change password</h2>
              <button onClick={onClose} className="text-text-secondary hover:text-text-primary text-lg leading-none">&times;</button>
            </div>

            <form onSubmit={handleSubmit} className="space-y-3">
              <div>
                <label htmlFor="cp-current" className="block text-xs font-medium text-text-secondary mb-1">Current password</label>
                <input
                  id="cp-current"
                  type="password"
                  autoComplete="current-password"
                  value={currentPassword}
                  onChange={(e) => { setCurrentPassword(e.target.value); clearError(); setValidationError(''); }}
                  className="w-full px-3 py-2 text-sm rounded border border-border-default bg-bg-primary text-text-primary focus:outline-none focus:border-accent-blue"
                  disabled={isLoading}
                />
              </div>
              <div>
                <label htmlFor="cp-new" className="block text-xs font-medium text-text-secondary mb-1">New password</label>
                <input
                  id="cp-new"
                  type="password"
                  autoComplete="new-password"
                  value={newPassword}
                  onChange={(e) => { setNewPassword(e.target.value); setValidationError(''); }}
                  className="w-full px-3 py-2 text-sm rounded border border-border-default bg-bg-primary text-text-primary focus:outline-none focus:border-accent-blue"
                  placeholder="Min 12 chars, mixed case + digit"
                  disabled={isLoading}
                />
              </div>
              <div>
                <label htmlFor="cp-confirm" className="block text-xs font-medium text-text-secondary mb-1">Confirm new password</label>
                <input
                  id="cp-confirm"
                  type="password"
                  autoComplete="new-password"
                  value={confirmPassword}
                  onChange={(e) => { setConfirmPassword(e.target.value); setValidationError(''); }}
                  className="w-full px-3 py-2 text-sm rounded border border-border-default bg-bg-primary text-text-primary focus:outline-none focus:border-accent-blue"
                  disabled={isLoading}
                />
              </div>

              {displayError && (
                <div className="text-xs text-red-400 bg-red-400/10 px-3 py-2 rounded">{displayError}</div>
              )}

              <button
                type="submit"
                disabled={isLoading || !currentPassword || !newPassword || !confirmPassword}
                className="w-full py-2 text-sm font-medium rounded bg-accent-blue text-white hover:bg-accent-blue/90 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
              >
                {isLoading ? 'Changing…' : 'Change password'}
              </button>
            </form>
          </>
        )}
      </div>
    </div>
  );
}
