import type { HubConnection } from '@microsoft/signalr';
import { useAuthStore } from '../stores/authStore';

/**
 * Register permission-change event handlers on the SignalR connection.
 * Call once when the hub connection is established.
 *
 * On PermissionsChanged, refetches /api/auth/me so the client's
 * assignedPipelineIds (and thus triggerable/viewOnly indicators) update live.
 * On reconnect, performs the same refetch to recover missed events.
 */
export function registerPermissionEvents(conn: HubConnection): void {
  conn.on('PermissionsChanged', () => {
    refetchCapabilities();
  });

  // Missed events while disconnected = stale indicators → refetch on reconnect
  conn.onreconnected(() => {
    refetchCapabilities();
  });
}

function refetchCapabilities(): void {
  const { fetchMe, isAuthenticated } = useAuthStore.getState();
  if (isAuthenticated) {
    fetchMe().catch((err) => {
      console.error('[PermissionEvents] Failed to refetch capabilities:', err?.error ?? err);
    });
  }
}
