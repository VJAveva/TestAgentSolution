import { useState, useRef, useEffect } from 'react';
import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import axios from 'axios';
import { useWatchListStore } from '../stores/watchlistStore';
import { useAgentStore } from '../stores/agentStore';
import { useExecutionStore } from '../stores/executionStore';
import { useConnectionStore } from '../stores/connectionStore';
import type { NodeStatus, WatchListConfig } from '../types/api';

export function useSignalR(): HubConnection | null {
  const [connection, setConnection] = useState<HubConnection | null>(null);
  const started = useRef(false);

  useEffect(() => {
    if (started.current) return;
    started.current = true;

    useConnectionStore.getState().setStatus('connecting');

    const conn = new HubConnectionBuilder()
      .withUrl('/hub/live')
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Warning)
      .build();

    conn.onreconnecting(() => useConnectionStore.getState().setStatus('connecting'));
    conn.onreconnected(() => useConnectionStore.getState().setStatus('connected'));
    conn.onclose(() => useConnectionStore.getState().setStatus('disconnected'));

    conn.on('Connected', (id: string) => {
      console.log('[SignalR] Connected:', id);
    });

    conn.on('ExecutionLog', (entry: { message?: string; agent?: string; sessionId?: string; timestamp?: string }) => {
      useExecutionStore.getState().addLog({
        message: entry.message ?? '',
        agent: entry.agent,
        sessionId: entry.sessionId,
        timestamp: entry.timestamp ?? new Date().toISOString(),
        severity: 'info',
      });
    });

    conn.on('AgentStatus', (name: string, status: string) => {
      useAgentStore.getState().updateStatus(name, status);
    });

    conn.on('NodeProgress', (tag: string, status: string) => {
      const mapped: NodeStatus =
        status === 'Running' ? 'Running'
        : status === 'Success' ? 'Success'
        : status === 'Failed' ? 'Failed'
        : 'Idle';
      useWatchListStore.getState().updateNodeStatus(tag, mapped);
    });

    conn.on('ExecutionEvent', (agent: string, line: string, kind: string) => {
      useExecutionStore.getState().addLog({
        message: line,
        agent,
        timestamp: new Date().toISOString(),
        kind: kind as 'stdout' | 'stderr',
        severity: kind === 'stderr' ? 'error' : 'info',
      });
    });

    conn.on('SessionProgress', (session: {
      sessionId?: string; watchItemTag?: string; state?: string;
      completedActions?: number; totalActions?: number;
      passedActions?: number; failedActions?: number; progressPercent?: number;
    }) => {
      const store = useExecutionStore.getState();
      const updated = store.sessions.map(s =>
        s.sessionId === session.sessionId
          ? { ...s, ...session } as typeof s
          : s
      );
      useExecutionStore.setState({ sessions: updated });
    });

    conn.on('TriggerFired', (path: string, file: string) => {
      useExecutionStore.getState().addLog({
        message: `Trigger: ${path} > ${file}`,
        timestamp: new Date().toISOString(),
        severity: 'success',
      });
    });

    // WatchList hot-reload: refetch tree when server signals config change
    conn.on('WatchListReloaded', async () => {
      try {
        const { data } = await axios.get<WatchListConfig>('/api/watchlist');
        useWatchListStore.getState().setConfig(data);
      } catch (err) {
        console.error('[SignalR] WatchListReloaded refetch failed:', err);
      }
    });

    conn.start().then(() => {
      setConnection(conn);
      useConnectionStore.getState().setConnection(conn);
      useConnectionStore.getState().setStatus('connected');
    }).catch(err => {
      console.error('[SignalR] Connection failed:', err);
      useConnectionStore.getState().setStatus('disconnected');
    });

    return () => {
      started.current = false;
      useConnectionStore.getState().setConnection(null);
      useConnectionStore.getState().setStatus('disconnected');
      conn.stop().catch(err => console.error('[SignalR] Stop error:', err));
    };
  }, []);

  return connection;
}
