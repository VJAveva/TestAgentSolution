import { useEffect } from 'react';
import AppShell from './components/layout/AppShell';
import AppLogPanel from './components/layout/AppLogPanel';
import ErrorBoundary from './components/layout/ErrorBoundary';
import SessionReconnector from './components/execution/SessionReconnector';
import { ExecutionDashboardProvider } from './hooks/useExecutionDashboard';
import { useSignalR } from './hooks/useSignalR';

export default function App() {
  const connection = useSignalR();

  useEffect(() => {
    if (connection) {
      console.log('[App] SignalR connection established');
    }
  }, [connection]);

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
