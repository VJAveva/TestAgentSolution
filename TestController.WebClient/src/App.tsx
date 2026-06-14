import { useEffect, useState } from 'react';
import AppShell from './components/layout/AppShell';
import AppLogPanel from './components/layout/AppLogPanel';
import ErrorBoundary from './components/layout/ErrorBoundary';
import SessionReconnector from './components/execution/SessionReconnector';
import { ExecutionDashboardProvider } from './hooks/useExecutionDashboard';
import { useSignalR } from './hooks/useSignalR';
import { useSystemModeStore } from './stores/systemModeStore';
import { useAuthStore } from './stores/authStore';
import LoginView from './views/LoginView';
import ChangePasswordView from './views/ChangePasswordView';

export default function App() {
  const isSecured = useSystemModeStore((s) => s.isSecured);
  const isDefault = useSystemModeStore((s) => s.isDefault);
  const isModeLoading = useSystemModeStore((s) => s.isLoading);
  const fetchMode = useSystemModeStore((s) => s.fetchMode);
  const { isAuthenticated, mustChangePassword, fetchMe, token, user } = useAuthStore();

  // Defer SignalR connection in Secured mode until the user is authenticated.
  // In Default mode (no auth), connect immediately.
  const connection = useSignalR(isDefault || isAuthenticated);
  const [initialized, setInitialized] = useState(false);
  const [bootError, setBootError] = useState<string | null>(null);

  // Fetch system mode on mount
  useEffect(() => {
    fetchMode()
      .then(() => setInitialized(true))
      .catch((err) => setBootError(err?.detail ?? err?.error ?? err?.message ?? 'Failed to load system mode'));
  }, [fetchMode]);

  // Hydrate auth from sessionStorage token on mount (Secured mode only)
  useEffect(() => {
    if (initialized && isSecured && token && !useAuthStore.getState().user) {
      fetchMe();
    }
  }, [initialized, isSecured, token, fetchMe]);

  useEffect(() => {
    if (connection) {
      console.log('[App] SignalR connection established');
    }
  }, [connection]);

  // Fatal bootstrap error
  if (bootError) {
    return (
      <div className="flex items-center justify-center h-screen">
        <div className="text-center space-y-2">
          <p className="text-red-400 font-medium">Bootstrap Error</p>
          <p className="text-sm text-text-secondary">{bootError}</p>
          <button className="text-xs text-blue-400 underline" onClick={() => window.location.reload()}>
            Retry
          </button>
        </div>
      </div>
    );
  }

  // Show loading until mode is resolved
  if (!initialized || isModeLoading) {
    return (
      <div className="flex items-center justify-center h-screen">
        <p className="text-sm text-text-secondary animate-pulse">Loading…</p>
      </div>
    );
  }

  // Default mode: skip login entirely, go straight to app shell
  if (isDefault) {
    return (
      <ErrorBoundary>
        <ExecutionDashboardProvider>
          <SessionReconnector />
          <AppShell />
          <AppLogPanel />
        </ExecutionDashboardProvider>
      </ErrorBoundary>
    );
  }

  // Secured mode: require authentication
  if (!isAuthenticated) {
    return <LoginView />;
  }

  // Token exists but user data not yet loaded (fetchMe in progress)
  if (!user) {
    return (
      <div className="flex items-center justify-center h-screen">
        <p className="text-sm text-text-secondary animate-pulse">Authenticating…</p>
      </div>
    );
  }

  // Forced password change before any other navigation
  if (mustChangePassword) {
    return <ChangePasswordView />;
  }

  return (
    <ErrorBoundary>
      <ExecutionDashboardProvider>
        <SessionReconnector />
        <AppShell />
        <AppLogPanel />
      </ExecutionDashboardProvider>
    </ErrorBoundary>
  );
}
