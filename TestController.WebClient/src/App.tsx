import { useEffect } from 'react';
import AppShell from './components/layout/AppShell';
import SessionReconnector from './components/execution/SessionReconnector';
import { useSignalR } from './hooks/useSignalR';

export default function App() {
  const connection = useSignalR();

  useEffect(() => {
    if (connection) {
      console.log('[App] SignalR connection established');
    }
  }, [connection]);

  return (
    <>
      <SessionReconnector />
      <AppShell />
    </>
  );
}
