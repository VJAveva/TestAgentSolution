import { useState, useRef, useEffect, useCallback } from 'react';
import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr';
import axios from 'axios';
import { useWatchListStore } from '../stores/watchlistStore';
import { useAgentStore } from '../stores/agentStore';
import { useExecutionStore } from '../stores/executionStore';
import { useConnectionStore } from '../stores/connectionStore';
import { getUserId } from '../lib/userIdentity';
import { appLogger } from '../lib/logger';
import type { NodeStatus, WatchListConfig } from '../types/api';

/** Tracks joined sessions for auto-rejoin after reconnect. */
const joinedSessions = new Set<string>();

/** Tracks current active session ID for ActionProgress events that lack sessionId. */
let _activeSessionId = '';

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
      appLogger.warn('SignalR', `Reconnecting: ${error?.message || 'unknown'}`);
      useConnectionStore.getState().setStatus('connecting');
    });
    conn.onreconnected((connectionId) => {
      console.log('[SignalR] Reconnected:', connectionId);
      appLogger.info('SignalR', `Reconnected (${connectionId})`);
      useConnectionStore.getState().setStatus('connected');
      // Rejoin all session groups after reconnect
      for (const sid of joinedSessions) {
        conn.invoke('JoinSession', sid).catch(() => {});
      }
      conn.invoke('JoinAsUser', getUserId()).catch(() => {});
    });
    conn.onclose((error) => {
      console.error('[SignalR] Connection closed:', error?.message);
      appLogger.error('SignalR', `Connection closed: ${error?.message || 'clean shutdown'}`);
      useConnectionStore.getState().setStatus('disconnected');
    });

    // ?? ControllerHub events (matches SignalRBridge.cs broadcasts) ??

    // Per-action status: fired by ActionPipelineExecutor.NodeProgress
    conn.on('ActionProgress', (data: {
      actionTag?: string; agentName?: string; command?: string; status?: string;
      sessionId?: string; progressPercent?: number;
    }) => {
      const tag = data.actionTag || data.command || '';
      const mapped: NodeStatus =
        data.status === 'Running' ? 'Running'
        : data.status === 'Success' ? 'Success'
        : data.status === 'Failed' ? 'Failed'
        : 'Idle';
      useWatchListStore.getState().updateNodeStatus(tag, mapped);

      // Feed pipeline store for Monitor dashboard
      if (data.agentName && data.actionTag) {
        const sid = data.sessionId || _activeSessionId || '';
        useExecutionStore.getState().updateActionProgress(
          sid,
          data.agentName,
          data.actionTag,
          data.command || data.actionTag,
          data.status || 'Running',
          data.progressPercent,
        );
      }
    });

    // ActionGroup status
    conn.on('GroupProgress', (data: { groupTag?: string; status?: string; sessionId?: string }) => {
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
      userId?: string; lockedAgents?: string[]; startTime?: string;
    }) => {
      if (data.watchItemTag) {
        useWatchListStore.getState().updateNodeStatus(data.watchItemTag, 'Running');
      }
      // Track active session ID for ActionProgress events that may lack sessionId
      if (data.sessionId) _activeSessionId = data.sessionId;

      // Initialize pipeline tracking
      useExecutionStore.getState().initSession(
        data.sessionId || '',
        data.watchItemTag || '',
        data.userId || '',
        data.lockedAgents || [],
        data.startTime || new Date().toISOString(),
      );

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
      // Mark pipeline session as completed
      if (data.sessionId) {
        useExecutionStore.getState().completeSession(data.sessionId, data.state || 'Completed');
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
    conn.on('AgentHeartbeats', (batch: { agentName?: string; state?: string; cpuUsagePct?: number; memoryUsedMb?: number; memoryTotalMb?: number; diskFreeGb?: number; timestamp?: string }[]) => {
      if (Array.isArray(batch)) {
        for (const hb of batch) {
          if (hb.agentName) {
            useExecutionStore.getState().updateHeartbeat({
              agentName: hb.agentName,
              state: hb.state || 'Idle',
              cpuUsagePct: hb.cpuUsagePct,
              memoryUsedMb: hb.memoryUsedMb,
              memoryTotalMb: hb.memoryTotalMb,
              diskFreeGb: hb.diskFreeGb,
              timestamp: hb.timestamp || new Date().toISOString(),
            });
          }
        }
      }
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

    // Agent lock state changes: broadcast to all clients
    conn.on('AgentLocksChanged', (data: { locks?: any[]; reason?: string }) => {
      window.dispatchEvent(new CustomEvent('agent-locks-changed', {
        detail: { locks: data.locks || [], reason: data.reason },
      }));
    });

    conn.start().then(() => {
      console.log('[SignalR] Connected to /hubs/controller');
      appLogger.info('SignalR', 'Connected to /hubs/controller');
      setConnection(conn);
      useConnectionStore.getState().setConnection(conn);
      useConnectionStore.getState().setStatus('connected');
      // Join user-specific group for filtered events
      conn.invoke('JoinAsUser', getUserId()).catch(() => {});
    }).catch(err => {
      console.error('[SignalR] Connection failed:', err.message);
      appLogger.error('SignalR', `Connection failed: ${err.message}`);
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

/** Join a session's SignalR group for filtered log events. */
export function joinSession(conn: HubConnection | null, sessionId: string) {
  joinedSessions.add(sessionId);
  conn?.invoke('JoinSession', sessionId).catch(() => {});
}

/** Leave a session's SignalR group. */
export function leaveSession(conn: HubConnection | null, sessionId: string) {
  joinedSessions.delete(sessionId);
  conn?.invoke('LeaveSession', sessionId).catch(() => {});
}
