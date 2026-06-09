import { useConnectionStore, type SignalRStatus } from '../../stores/connectionStore';
import { appLogger } from '../../lib/logger';
import { useEffect, useState } from 'react';

const statusConfig: Record<SignalRStatus, { dot: string; label: string }> = {
  connected:    { dot: 'bg-acc-green', label: 'Live' },
  connecting:   { dot: 'bg-acc-yellow animate-pulse', label: 'Reconnecting...' },
  disconnected: { dot: 'bg-acc-red', label: 'Offline' },
};

export default function ConnectionStatus() {
  const status = useConnectionStore(s => s.status);
  const cfg = statusConfig[status];
  const [errorCount, setErrorCount] = useState(0);

  useEffect(() => {
    setErrorCount(appLogger.counts().error);
    return appLogger.subscribe(() => setErrorCount(appLogger.counts().error));
  }, []);

  return (
    <div className="flex items-center gap-2 text-[10px] text-text-muted">
      <span className={`w-1.5 h-1.5 rounded-full ${cfg.dot}`} />
      {cfg.label}
      {errorCount > 0 && (
        <span className="ml-1 px-1.5 py-0.5 rounded bg-acc-red/20 text-acc-red font-semibold"
              title={`${errorCount} client error(s) captured`}>
          {errorCount} err
        </span>
      )}
    </div>
  );
}
