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
      .withUrl('/hubs/controller')
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Warning)
      .build();

    conn.onreconnecting((error) => {
      console.warn('[SignalR] Reconnecting...', error?.message);
      useConnectionStore.getState().setStatus('connecting');
    });
    conn.onreconnected((connectionId) => {
      console.log('[SignalR] Reconnected:', connectionId);
      useConnectionStore.getState().setStatus('connected');
    });
    conn.onclose((error) => {
      console.error('[SignalR] Connection closed:', error?.message);
      useConnectionStore.getState().setStatus('disconnected');
    });

    // ?? ControllerHub events (matches SignalRBridge.cs broadcasts) ??

    // Per-action status: fired by ActionPipelineExecutor.NodeProgress
    conn.on('ActionProgress', (data: {
      actionTag?: string; agentName?: string; command?: string; status?: string;
    }) => {
      const tag = data.actionTag || data.command || '';
      const mapped: NodeStatus =
        data.status === 'Running' ? 'Running'
        : data.status === 'Success' ? 'Success'
        : data.status === 'Failed' ? 'Failed'
        : 'Idle';
      useWatchListStore.getState().updateNodeStatus(tag, mapped);
    });

    // ActionGroup status
    conn.on('GroupProgress', (data: { groupTag?: string; status?: string }) => {
      if (data.groupTag) {
        const mapped: NodeStatus =
          data.status === 'Running' ? 'Running'
          : data.status === 'Success' ? 'Success'
          : data.status === 'Failed' ? 'Failed'
          : 'Idle';
        useWatchListStore.getState().updateNodeStatus(data.groupTag, mapped);
      }
    });

    // Execution lifecycle: fired by ExecutionController and MainViewModel
    conn.on('ExecutionStarted', (data: {
      sessionId?: string; watchItemTag?: string; eventType?: string;
    }) => {
      if (data.watchItemTag) {
        useWatchListStore.getState().updateNodeStatus(data.watchItemTag, 'Running');
      }
      useExecutionStore.getState().addLog({
        message: `Execution started: ${data.watchItemTag} (${data.eventType})`,
        sessionId: data.sessionId,
        timestamp: new Date().toISOString(),
        severity: 'info',
      });
    });

    conn.on('ExecutionCompleted', (data: {
      sessionId?: string; watchItemTag?: string; state?: string;
    }) => {
      if (data.watchItemTag) {
        const mapped: NodeStatus =
          data.state === 'Success' ? 'Success'
          : data.state === 'Failed' || data.state === 'PartialFailure' ? 'Failed'
          : data.state === 'Cancelled' ? 'Idle'
          : 'Idle';
        useWatchListStore.getState().updateNodeStatus(data.watchItemTag, mapped);
      }
      useExecutionStore.getState().addLog({
        message: `Execution ${data.state}: ${data.watchItemTag}`,
        sessionId: data.sessionId,
        timestamp: new Date().toISOString(),
        severity: data.state === 'Success' ? 'success' : data.state === 'Failed' ? 'error' : 'warning',
      });
    });

    // Pipeline log entries
    conn.on('LogEntry', (entry: {
      timestamp?: string; sessionId?: string; severity?: string;
      category?: string; agentName?: string; message?: string;
    }) => {
      useExecutionStore.getState().addLog({
        message: entry.message ?? '',
        agent: entry.agentName || entry.category,
        sessionId: entry.sessionId,
        timestamp: entry.timestamp ?? new Date().toISOString(),
        severity: (entry.severity?.toLowerCase() as 'info' | 'success' | 'warning' | 'error') || 'info',
      });
    });

    // Agent output (stdout/stderr from remote agents)
    conn.on('AgentOutput', (data: {
      agentName?: string; line?: string; kind?: string;
    }) => {
      useExecutionStore.getState().addLog({
        message: data.line ?? '',
        agent: data.agentName,
        timestamp: new Date().toISOString(),
        kind: data.kind as 'stdout' | 'stderr',
        severity: data.kind === 'stderr' ? 'error' : 'info',
      });
    });

    // Agent lifecycle
    conn.on('AgentRegistered', (data: { agentName?: string }) => {
      if (data.agentName) useAgentStore.getState().updateStatus(data.agentName, 'Connected');
    });
    conn.on('AgentUnregistered', (data: { agentName?: string }) => {
      if (data.agentName) useAgentStore.getState().updateStatus(data.agentName, 'Disconnected');
    });
    conn.on('AgentStatusChanged', (data: { agentName?: string; status?: string }) => {
      if (data.agentName && data.status) useAgentStore.getState().updateStatus(data.agentName, data.status);
    });
    conn.on('AgentHeartbeats', (_batch: unknown[]) => {
      // Heartbeat payloads handled by agent detail components if needed
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
      console.log('[SignalR] Connected to /hubs/controller');
      setConnection(conn);
      useConnectionStore.getState().setConnection(conn);
      useConnectionStore.getState().setStatus('connected');
    }).catch(err => {
      console.error('[SignalR] Connection failed:', err.message);
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
