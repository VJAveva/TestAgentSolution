import type { HubConnection } from '@microsoft/signalr';
import { useLockStore, type PipelineLockDto } from '../stores/lockStore';
import { apiFetch } from '../lib/api';

/**
 * Register pipeline lock event handlers on the SignalR connection.
 * Call once when the hub connection is established.
 *
 * On reconnect, performs a full resync via GET /api/locks to recover
 * any events missed while disconnected.
 */
export function registerLockEvents(conn: HubConnection): void {
  const store = useLockStore;

  conn.on('PipelineLockAcquired', (dto: PipelineLockDto) => {
    store.getState().onAcquired(dto);
  });

  conn.on('PipelineLockReleased', (dto: PipelineLockDto) => {
    store.getState().onReleased(dto);
  });

  conn.on('PipelineLockExpired', (dto: PipelineLockDto) => {
    store.getState().onExpired(dto);
  });

  // Admin force-release. The server wraps the payload as
  // { lock: PipelineLockDto, priorOwnerDisplayName: string }, so unwrap to the
  // inner lock DTO. Tolerate Pascal/camel casing and a flat fallback.
  const unwrapLock = (payload: unknown): PipelineLockDto => {
    const p = payload as { lock?: PipelineLockDto; Lock?: PipelineLockDto };
    return (p?.lock ?? p?.Lock ?? (payload as PipelineLockDto));
  };

  conn.on('PipelineLockForceReleased', (payload: unknown) => {
    store.getState().onForceReleased(unwrapLock(payload));
  });

  // Legacy/defensive alias — the server does not currently emit this name, but
  // keep the handler so an older controller build still clears the lock.
  conn.on('PipelineLockStolen', (payload: unknown) => {
    store.getState().onForceReleased(unwrapLock(payload));
  });

  conn.on('PipelineLockRewritten', (payload: unknown) => {
    store.getState().onRewritten(unwrapLock(payload));
  });

  // Full resync on reconnect — events missed while disconnected = stale badges
  conn.onreconnected(() => {
    resyncLocks();
  });

  // Initial sync on first connect
  resyncLocks();
}

function resyncLocks(): void {
  apiFetch<PipelineLockDto[]>('/api/locks')
    .then((data) => {
      useLockStore.getState().setAll(data);
    })
    .catch((err) => {
      console.error('[LockEvents] Failed to resync locks:', err?.error ?? err);
    });
}
