import { useEffect } from 'react';
import AppShell from './components/layout/AppShell';
import AppLogPanel from './components/layout/AppLogPanel';
import SessionReconnector from './components/execution/SessionReconnector';
import { useSignalR } from './hooks/useSignalR';
import { appLogger } from './lib/logger';

export default function App() {
  const connection = useSignalR();

  useEffect(() => {
    if (connection) {
      appLogger.info('SignalR', 'Connection established');
    }
  }, [connection]);

  return (
    <>
      <SessionReconnector />
      <AppShell />
      <AppLogPanel />
    </>
  );
}
