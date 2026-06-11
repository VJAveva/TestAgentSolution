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
  const connection = useSignalR();
  const isSecured = useSystemModeStore((s) => s.isSecured);
  const isDefault = useSystemModeStore((s) => s.isDefault);
  const isModeLoading = useSystemModeStore((s) => s.isLoading);
  const fetchMode = useSystemModeStore((s) => s.fetchMode);
  const { isAuthenticated, mustChangePassword, fetchMe, token } = useAuthStore();
  const [initialized, setInitialized] = useState(false);

  // Fetch system mode on mount
  useEffect(() => {
    fetchMode().then(() => setInitialized(true));
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

  // Show nothing until mode is resolved
  if (!initialized || isModeLoading) {
    return null;
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
