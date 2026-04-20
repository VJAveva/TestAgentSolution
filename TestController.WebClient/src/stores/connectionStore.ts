import { create } from 'zustand';
import type { HubConnection } from '@microsoft/signalr';

export type SignalRStatus = 'connecting' | 'connected' | 'disconnected';

interface ConnectionState {
  connection: HubConnection | null;
  status: SignalRStatus;
  setConnection: (conn: HubConnection | null) => void;
  setStatus: (status: SignalRStatus) => void;
}

export const useConnectionStore = create<ConnectionState>((set) => ({
  connection: null,
  status: 'connecting',
  setConnection: (connection) => set({ connection }),
  setStatus: (status) => set({ status }),
}));
