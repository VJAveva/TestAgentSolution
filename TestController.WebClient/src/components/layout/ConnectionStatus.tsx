import { useConnectionStore, type SignalRStatus } from '../../stores/connectionStore';

const statusConfig: Record<SignalRStatus, { dot: string; label: string }> = {
  connected:    { dot: 'bg-acc-green', label: 'Live' },
  connecting:   { dot: 'bg-acc-yellow animate-pulse', label: 'Reconnecting...' },
  disconnected: { dot: 'bg-acc-red', label: 'Offline' },
};

export default function ConnectionStatus() {
  const status = useConnectionStore(s => s.status);
  const cfg = statusConfig[status];

  return (
    <div className="flex items-center gap-1.5 text-[10px] text-text-muted">
      <span className={`w-1.5 h-1.5 rounded-full ${cfg.dot}`} />
      {cfg.label}
    </div>
  );
}
