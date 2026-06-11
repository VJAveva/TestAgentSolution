import { useState, FormEvent } from 'react';
import { useAuthStore } from '../stores/authStore';

/**
 * Login screen per Mockup 1 — username, password, Sign In.
 * "Continue as Guest" secondary button for Web Client.
 */
export default function LoginView() {
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const { login, loginAsGuest, isLoading, error, clearError } = useAuthStore();

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault();
    if (!username.trim() || !password.trim()) return;
    await login(username.trim(), password);
  };

  const handleGuest = async () => {
    await loginAsGuest();
  };

  return (
    <div className="min-h-screen flex items-center justify-center bg-bg-primary">
      <div className="w-full max-w-sm p-8 rounded-lg border border-border-default bg-bg-secondary shadow-lg">
        {/* Brand mark */}
        <div className="flex flex-col items-center mb-6">
          <div className="w-12 h-12 rounded-full bg-accent-blue/20 flex items-center justify-center mb-3">
            <svg className="w-6 h-6 text-accent-blue" fill="currentColor" viewBox="0 0 20 20">
              <path fillRule="evenodd" d="M2.166 4.999A11.954 11.954 0 0010 1.944 11.954 11.954 0 0017.834 5c.11.65.166 1.32.166 2.001 0 5.225-3.34 9.67-8 11.317C5.34 16.67 2 12.225 2 7c0-.682.057-1.35.166-2.001zm11.541 3.708a1 1 0 00-1.414-1.414L9 10.586 7.707 9.293a1 1 0 00-1.414 1.414l2 2a1 1 0 001.414 0l4-4z" clipRule="evenodd" />
            </svg>
          </div>
          <h1 className="text-lg font-semibold text-text-primary">Test Controller</h1>
          <p className="text-xs text-text-secondary mt-1">Sign in to continue</p>
        </div>

        <form onSubmit={handleSubmit} className="space-y-4">
          <div>
            <label htmlFor="username" className="block text-xs font-medium text-text-secondary mb-1">
              Username
            </label>
            <input
              id="username"
              type="text"
              autoComplete="username"
              value={username}
              onChange={(e) => { setUsername(e.target.value); clearError(); }}
              className="w-full px-3 py-2 text-sm rounded border border-border-default bg-bg-primary text-text-primary focus:outline-none focus:border-accent-blue"
              placeholder="Enter username"
              disabled={isLoading}
            />
          </div>

          <div>
            <label htmlFor="password" className="block text-xs font-medium text-text-secondary mb-1">
              Password
            </label>
            <input
              id="password"
              type="password"
              autoComplete="current-password"
              value={password}
              onChange={(e) => { setPassword(e.target.value); clearError(); }}
              className="w-full px-3 py-2 text-sm rounded border border-border-default bg-bg-primary text-text-primary focus:outline-none focus:border-accent-blue"
              placeholder="Enter password"
              disabled={isLoading}
            />
          </div>

          {error && (
            <div className="text-xs text-red-400 bg-red-400/10 px-3 py-2 rounded">
              {error}
            </div>
          )}

          <button
            type="submit"
            disabled={isLoading || !username.trim() || !password.trim()}
            className="w-full py-2 text-sm font-medium rounded bg-accent-blue text-white hover:bg-accent-blue/90 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
          >
            {isLoading ? 'Signing in…' : 'Sign in'}
          </button>
        </form>

        {/* Divider */}
        <div className="flex items-center my-4">
          <div className="flex-1 border-t border-border-default" />
          <span className="px-3 text-xs text-text-secondary">or</span>
          <div className="flex-1 border-t border-border-default" />
        </div>

        {/* Guest login */}
        <button
          onClick={handleGuest}
          disabled={isLoading}
          className="w-full py-2 text-sm font-medium rounded border border-border-default text-text-secondary hover:text-text-primary hover:border-text-secondary disabled:opacity-50 transition-colors"
        >
          Continue as Guest
        </button>

        <p className="text-[10px] text-text-secondary text-center mt-4">
          Senior Manager, Engineer, or Guest
        </p>
      </div>
    </div>
  );
}
