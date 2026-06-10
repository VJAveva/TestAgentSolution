import { useState, useRef, useEffect, useCallback } from 'react';
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel, type IRetryPolicy, type RetryContext } from '@microsoft/signalr';
import axios from 'axios';
import { useWatchListStore } from '../stores/watchlistStore';
import { useAgentStore } from '../stores/agentStore';
import { useExecutionStore } from '../stores/executionStore';
import { useResultsStore } from '../stores/resultsStore';
import { useConnectionStore } from '../stores/connectionStore';
import { getUserId } from '../lib/userIdentity';
import { registerSystemModeEvents } from '../signalr/SystemModeEvents';
import type { NodeStatus, WatchListConfig } from '../types/api';

/** Tracks joined sessions for auto-rejoin after reconnect. */
const joinedSessions = new Set<string>();

/**
 * Indefinite reconnect policy with exponential backoff capped at 30s.
 * SignalR's default policy gives up after 4 attempts (~47s) and never
 * tries again ? that's the WebClient-goes-offline symptom: any transient
 * server hiccup or laptop sleep > 47s leaves the client permanently
 * disconnected until full page reload.
 *
 * Schedule: 0s, 2s, 5s, 10s, 15s, then every 30s forever.
 */
const indefiniteRetryPolicy: IRetryPolicy = {
  nextRetryDelayInMilliseconds(ctx: RetryContext): number {
    const ms = ctx.elapsedMilliseconds;
    if (ms < 1_000) return 0;
    if (ms < 5_000) return 2_000;
    if (ms < 15_000) return 5_000;
    if (ms < 30_000) return 10_000;
    if (ms < 60_000) return 15_000;
    return 30_000;
  },
};

export function useSignalR(): HubConnection | null {
  const [connection, setConnection] = useState<HubConnection | null>(null);
  const started = useRef(false);
  const unmountedRef = useRef(false);
  const restartTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const connRef = useRef<HubConnection | null>(null);

  // Defined here so the onclose handler (which lives inside useEffect) and
  // the visibility-change listener (also inside useEffect) can both call it.
  const tryStart = useCallback((conn: HubConnection) => {
    if (unmountedRef.current) return;
    if (conn.state !== HubConnectionState.Disconnected) return;
    const hubUrl = conn.baseUrl;
    console.log(`[SignalR] (re)starting connection to ${hubUrl}`);
    useConnectionStore.getState().setStatus('connecting');
    conn.start().then(() => {
      console.log(`[SignalR] Connected to ${hubUrl}`);
      setConnection(conn);
      useConnectionStore.getState().setConnection(conn);
      useConnectionStore.getState().setStatus('connected');
      // Rejoin all session groups + user group after a fresh connect
      for (const sid of joinedSessions) {
        conn.invoke('JoinSession', sid).catch(err =>
          console.warn(`[SignalR] JoinSession ${sid} failed:`, err?.message ?? err));
      }
      conn.invoke('JoinAsUser', getUserId()).catch(err =>
        console.warn('[SignalR] JoinAsUser failed:', err?.message ?? err));
      // Request browser notification permission for background session completion alerts
      if ('Notification' in window && Notification.permission === 'default') {
        Notification.requestPermission();
      }
    }).catch(err => {
      console.error(
        `[SignalR] start() failed: ${err?.message ?? err} ? retry in 30s`, err);
      useConnectionStore.getState().setStatus('disconnected');
      if (!unmountedRef.current) {
        restartTimerRef.current = setTimeout(() => tryStart(conn), 30_000);
      }
    });
  }, []);

  useEffect(() => {
    if (started.current) return;
    started.current = true;
    unmountedRef.current = false;

    useConnectionStore.getState().setStatus('connecting');

    // Hub URL must be absolute when WebClient and WebApi live on different
    // origins (production split-origin deployment). VITE_API_BASE_URL is
    // the same value used by apiFetch and the configured axios baseURL.
    // In dev (empty base) the relative URL goes through the Vite proxy.
    const apiBase = import.meta.env.VITE_API_BASE_URL || '';
    const hubUrl = `${apiBase}/hubs/controller`;
    console.log(`[SignalR] Connecting to ${hubUrl}`);

    const conn = new HubConnectionBuilder()
      .withUrl(hubUrl, {
        // Pass browser credentials (cookies/NTLM) for Domain/Local auth modes.
        // Harmless in None mode; Token mode would need a bearer token accessTokenFactory.
        withCredentials: true,
      })
      // Indefinite reconnect (see policy above) instead of the previous
      // 5-attempt array which gave up after ~47s and left the WebClient
      // permanently offline on any longer outage (sleep, server restart,
      // network blip).
      .withAutomaticReconnect(indefiniteRetryPolicy)
      .configureLogging(LogLevel.Warning)
      .build();

    // Match server-side SignalR timeouts from appsettings.json:
    //   KeepAliveInterval = 15s (server pings every 15s)
    //   ClientTimeoutInterval = 30s (server drops if no message for 30s)
    // Client must wait at least KeepAliveInterval * 2 before declaring the
    // server dead, otherwise spurious "offline" flashes occur.
    conn.serverTimeoutInMilliseconds = 60_000;     // 2 ? server keepalive
    conn.keepAliveIntervalInMilliseconds = 15_000; // match server

    conn.onreconnecting((error) => {
      console.warn('[SignalR] Reconnecting...', error?.message);
      useConnectionStore.getState().setStatus('connecting');
    });
    conn.onreconnected((connectionId) => {
      console.log('[SignalR] Reconnected:', connectionId);
      useConnectionStore.getState().setStatus('connected');
      // Rejoin all session groups after reconnect
      for (const sid of joinedSessions) {
        conn.invoke('JoinSession', sid).catch(() => {});
      }
      conn.invoke('JoinAsUser', getUserId()).catch(() => {});
    });
    conn.onclose((error) => {
      // Connection went past automatic reconnect (network down for very long,
      // or server explicitly closed it). Schedule a manual restart so the
      // WebClient does not stay permanently offline. We continue retrying
      // every 30s until either start() succeeds or the page is unloaded.
      console.error(`[SignalR] Connection closed: ${error?.message ?? 'no error'} ? will retry in 30s`);
      useConnectionStore.getState().setStatus('disconnected');
      if (!unmountedRef.current) {
        restartTimerRef.current = setTimeout(() => tryStart(conn), 30_000);
      }
    });

    // ?? ControllerHub events (matches SignalRBridge.cs broadcasts) ??

    // Per-action and per-group status: fired by ActionPipelineExecutor.NodeProgress.
    // Group progress is delivered via ActionProgress with a groupTag field.
    conn.on('ActionProgress', (data: {
      actionTag?: string; groupTag?: string; agentName?: string; command?: string; status?: string;
    }) => {
      const mapped: NodeStatus =
        data.status === 'Running' ? 'Running'
        : data.status === 'Success' ? 'Success'
        : data.status === 'Failed' ? 'Failed'
        : 'Idle';
      const actionTag = data.actionTag || data.command || '';
      if (actionTag) {
        useWatchListStore.getState().updateNodeStatus(actionTag, mapped);
      }
      if (data.groupTag) {
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

      // Browser notification when tab is hidden so QA doesn't miss completion
      if (document.hidden && 'Notification' in window && Notification.permission === 'granted') {
        const icon = data.state === 'Success' ? '✅' : data.state === 'Failed' ? '❌' : '⚠️';
        new Notification(`${icon} Pipeline ${data.state}`, {
          body: data.watchItemTag ?? data.sessionId ?? 'Unknown session',
          tag: `execution-${data.sessionId}`,
        });
      }
    });

    // Single-session cancel broadcasts ExecutionCancelled (not ExecutionCompleted)
    conn.on('ExecutionCancelled', (data: {
      sessionId?: string; watchItemTag?: string;
    }) => {
      if (data.watchItemTag) {
        useWatchListStore.getState().updateNodeStatus(data.watchItemTag, 'Idle');
      }
      useExecutionStore.getState().addLog({
        message: `Execution Cancelled: ${data.watchItemTag ?? data.sessionId}`,
        sessionId: data.sessionId,
        timestamp: new Date().toISOString(),
        severity: 'warning',
      });
    });

    // Results updated: re-fetch builds list when new results are available
    conn.on('ResultsUpdated', () => {
      axios.get('/api/results/builds').then(({ data }) => {
        useResultsStore.getState().setBuilds(data.items ?? []);
      }).catch(() => {});
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
      agentName?: string; line?: string; kind?: string; sessionId?: string;
    }) => {
      useExecutionStore.getState().addLog({
        message: data.line ?? '',
        agent: data.agentName,
        sessionId: data.sessionId,
        timestamp: new Date().toISOString(),
        kind: data.kind as 'stdout' | 'stderr',
        severity: data.kind === 'stderr' ? 'error' : 'info',
      });
    });

    // Batched output (scale fix: server batches lines every 500ms)
    conn.on('AgentOutputBatch', (batch: Array<{
      agentName?: string; line?: string; kind?: string; sessionId?: string;
    }>) => {
      const store = useExecutionStore.getState();
      for (const data of batch) {
        store.addLog({
          message: data.line ?? '',
          agent: data.agentName,
          sessionId: data.sessionId,
          timestamp: new Date().toISOString(),
          kind: data.kind as 'stdout' | 'stderr',
          severity: data.kind === 'stderr' ? 'error' : 'info',
        });
      }
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
    conn.on('AgentHeartbeats', (batch: Array<{ agentName?: string; state?: string }>) => {
      const store = useAgentStore.getState();
      for (const hb of batch) {
        if (hb.agentName && hb.state) {
          store.updateStatus(hb.agentName, hb.state);
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

    // Register SystemModeChanged handler for live mode switching
    registerSystemModeEvents(conn);

    connRef.current = conn;
    tryStart(conn);

    // Bring the connection back when the tab becomes visible again or the
    // browser regains network. SignalR's auto-reconnect can fail to fire
    // when the JS event loop was paused (laptop sleep, mobile background).
    const onVisible = () => {
      if (document.visibilityState === 'visible'
          && conn.state === HubConnectionState.Disconnected) {
        if (restartTimerRef.current) {
          clearTimeout(restartTimerRef.current);
          restartTimerRef.current = null;
        }
        tryStart(conn);
      }
    };
    const onOnline = () => {
      if (conn.state === HubConnectionState.Disconnected) {
        if (restartTimerRef.current) {
          clearTimeout(restartTimerRef.current);
          restartTimerRef.current = null;
        }
        tryStart(conn);
      }
    };
    document.addEventListener('visibilitychange', onVisible);
    window.addEventListener('online', onOnline);

    return () => {
      unmountedRef.current = true;
      started.current = false;
      document.removeEventListener('visibilitychange', onVisible);
      window.removeEventListener('online', onOnline);
      if (restartTimerRef.current) {
        clearTimeout(restartTimerRef.current);
        restartTimerRef.current = null;
      }
      useConnectionStore.getState().setConnection(null);
      useConnectionStore.getState().setStatus('disconnected');
      conn.stop().catch(err => console.error('[SignalR] Stop error:', err));
      connRef.current = null;
    };
  }, [tryStart]);

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
