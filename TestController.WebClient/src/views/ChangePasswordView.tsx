import { useState, FormEvent } from 'react';
import { useAuthStore } from '../stores/authStore';

/**
 * First-login forced password change view.
 * Requires current (initial) password + new password + confirmation.
 * Client-side validation: min 12 chars, mixed case + digit.
 */
export default function ChangePasswordView() {
  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [validationError, setValidationError] = useState('');
  const { changePassword, isLoading, error, clearError } = useAuthStore();

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
    await changePassword(currentPassword, newPassword);
  };

  const displayError = validationError || error;

  return (
    <div className="min-h-screen flex items-center justify-center bg-bg-primary">
      <div className="w-full max-w-sm p-8 rounded-lg border border-border-default bg-bg-secondary shadow-lg">
        {/* Header */}
        <div className="flex flex-col items-center mb-6">
          <div className="w-12 h-12 rounded-full bg-amber-400/20 flex items-center justify-center mb-3">
            <svg className="w-6 h-6 text-amber-400" fill="currentColor" viewBox="0 0 20 20">
              <path fillRule="evenodd" d="M5 9V7a5 5 0 0110 0v2a2 2 0 012 2v5a2 2 0 01-2 2H5a2 2 0 01-2-2v-5a2 2 0 012-2zm8-2v2H7V7a3 3 0 016 0z" clipRule="evenodd" />
            </svg>
          </div>
          <h1 className="text-lg font-semibold text-text-primary">Change password</h1>
          <p className="text-xs text-text-secondary mt-1 text-center">
            You must change your password before continuing
          </p>
        </div>

        <form onSubmit={handleSubmit} className="space-y-4">
          <div>
            <label htmlFor="current-password" className="block text-xs font-medium text-text-secondary mb-1">
              Current password
            </label>
            <input
              id="current-password"
              type="password"
              autoComplete="current-password"
              value={currentPassword}
              onChange={(e) => { setCurrentPassword(e.target.value); clearError(); setValidationError(''); }}
              className="w-full px-3 py-2 text-sm rounded border border-border-default bg-bg-primary text-text-primary focus:outline-none focus:border-accent-blue"
              placeholder="Enter current password"
              disabled={isLoading}
            />
          </div>

          <div>
            <label htmlFor="new-password" className="block text-xs font-medium text-text-secondary mb-1">
              New password
            </label>
            <input
              id="new-password"
              type="password"
              autoComplete="new-password"
              value={newPassword}
              onChange={(e) => { setNewPassword(e.target.value); setValidationError(''); }}
              className="w-full px-3 py-2 text-sm rounded border border-border-default bg-bg-primary text-text-primary focus:outline-none focus:border-accent-blue"
              placeholder="Min 12 characters, mixed case + digit"
              disabled={isLoading}
            />
          </div>

          <div>
            <label htmlFor="confirm-password" className="block text-xs font-medium text-text-secondary mb-1">
              Confirm new password
            </label>
            <input
              id="confirm-password"
              type="password"
              autoComplete="new-password"
              value={confirmPassword}
              onChange={(e) => { setConfirmPassword(e.target.value); setValidationError(''); }}
              className="w-full px-3 py-2 text-sm rounded border border-border-default bg-bg-primary text-text-primary focus:outline-none focus:border-accent-blue"
              placeholder="Re-enter new password"
              disabled={isLoading}
            />
          </div>

          {displayError && (
            <div className="text-xs text-red-400 bg-red-400/10 px-3 py-2 rounded">
              {displayError}
            </div>
          )}

          <button
            type="submit"
            disabled={isLoading || !currentPassword || !newPassword || !confirmPassword}
            className="w-full py-2 text-sm font-medium rounded bg-accent-blue text-white hover:bg-accent-blue/90 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
          >
            {isLoading ? 'Changing…' : 'Change password'}
          </button>
        </form>

        <p className="text-[10px] text-text-secondary text-center mt-4">
          Requirements: 12+ characters, uppercase, lowercase, and a digit
        </p>
      </div>
    </div>
  );
}
