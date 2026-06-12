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

  conn.on('PipelineLockStolen', (dto: PipelineLockDto) => {
    store.getState().onStolen(dto);
  });

  conn.on('PipelineLockRewritten', (dto: PipelineLockDto) => {
    store.getState().onRewritten(dto);
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
