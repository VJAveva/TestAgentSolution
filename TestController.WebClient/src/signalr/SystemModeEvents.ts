import type { HubConnection } from '@microsoft/signalr';
import { useSystemModeStore, type SystemMode } from '../stores/systemModeStore';

/**
 * Register SystemModeChanged event handler on the SignalR connection.
 * Call once when the hub connection is established.
 *
 * On receiving the event: fetches the latest mode from the server then
 * triggers a page reload if the mode actually changed.
 *
 * Also polls /api/system/mode on reconnect as a fallback per
 * 02_Implementation_Roadmap.md Phase 0.5 Risks table.
 */
export function registerSystemModeEvents(conn: HubConnection): void {
  conn.on('SystemModeChanged', (data: { mode?: string }) => {
    const mode = data.mode as SystemMode | undefined;
    if (mode) {
      useSystemModeStore.getState().onModeChanged(mode);
    } else {
      // Fallback: fetch from server if payload is unexpected
      useSystemModeStore.getState().fetchMode().then(() => {
        // If mode changed, onModeChanged would have triggered reload
        // via the store's setMode logic
      });
    }
  });

  // On reconnect, poll mode in case the event was missed during disconnect
  conn.onreconnected(() => {
    useSystemModeStore.getState().fetchMode();
  });
}
