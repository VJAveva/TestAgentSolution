import { memo, useRef } from 'react';
import { useRenderCount } from '../../hooks/useRenderCount';
import { useVirtualizer } from '@tanstack/react-virtual';
import { Server, Lock, Unlock, Wifi, WifiOff, RefreshCw } from 'lucide-react';
import { useFleetState } from '../../hooks/useFleetState';
import { isAgentOnline } from '../../lib/agentStatus';
import FleetBulkActions from './FleetBulkActions';
import type { FleetAgent } from '../../types/agentWorkspace';

const COLS = 3; // Cards per row (matches xl:grid-cols-3)
const ROW_HEIGHT = 140; // Approximate card height in px

interface FleetPageProps {
  onSelectAgent: (name: string) => void;
}

export default function FleetPage({ onSelectAgent }: FleetPageProps) {
  useRenderCount('FleetPage');
  const { fleet, loading, error, refresh } = useFleetState();
  const parentRef = useRef<HTMLDivElement>(null);

  const rowCount = Math.ceil(fleet.length / COLS);
  const virtualizer = useVirtualizer({
    count: rowCount,
    getScrollElement: () => parentRef.current,
    estimateSize: () => ROW_HEIGHT,
    overscan: 3,
  });

  if (loading) {
    return <div className="flex items-center justify-center h-full text-text-muted text-sm">Loading fleet…</div>;
  }

  if (error) {
    return (
      <div className="flex flex-col items-center justify-center h-full gap-2">
        <p className="text-xs text-acc-red">{error}</p>
        <button className="text-xs text-accent hover:underline" onClick={refresh}>Retry</button>
      </div>
    );
  }

  return (
    <div className="h-full flex flex-col">
      <div className="flex items-center justify-between px-4 py-2 border-b border-bdr">
        <h2 className="text-sm font-semibold text-text-primary">Fleet Overview</h2>
        <div className="flex items-center gap-1">
          <FleetBulkActions
            agents={fleet.map(a => ({ name: a.name, address: a.address }))}
            onChanged={refresh}
          />
          <div className="w-px h-4 bg-bdr mx-1" />
          <button
            className="flex items-center gap-1 px-2 py-1 rounded text-xs text-text-muted hover:bg-white/5"
            onClick={refresh}
          >
            <RefreshCw size={12} /> Refresh
          </button>
        </div>
      </div>

      {fleet.length === 0 ? (
        <div className="flex-1 flex items-center justify-center text-text-muted text-xs">No agents registered.</div>
      ) : (
        <div ref={parentRef} className="flex-1 overflow-auto p-4">
          <div style={{ height: virtualizer.getTotalSize(), position: 'relative' }}>
            {virtualizer.getVirtualItems().map(virtualRow => {
              const startIdx = virtualRow.index * COLS;
              const rowAgents = fleet.slice(startIdx, startIdx + COLS);
              return (
                <div
                  key={virtualRow.key}
                  data-index={virtualRow.index}
                  ref={virtualizer.measureElement}
                  className="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-3 pb-3"
                  style={{
                    position: 'absolute',
                    top: 0,
                    left: 0,
                    right: 0,
                    transform: `translateY(${virtualRow.start}px)`,
                  }}
                >
                  {rowAgents.map(agent => (
                    <FleetCard key={agent.name} agent={agent} onSelect={onSelectAgent} />
                  ))}
                </div>
              );
            })}
          </div>
        </div>
      )}
    </div>
  );
}

// Scale fix: Memoize FleetCard to avoid re-rendering all 200 cards when one agent changes.
// Without memo, React re-renders every card on each fleet state update.
const FleetCard = memo(function FleetCard({ agent, onSelect }: { agent: FleetAgent; onSelect: (name: string) => void }) {
  const isOnline = isAgentOnline(agent.status);
  const isBusy = agent.isLocked;
  const isExecuting = isBusy || (agent.status?.toLowerCase().startsWith('executing') ?? false);
  const hasFailure = agent.status?.toLowerCase().startsWith('failed') ?? false;

  const borderColor = isExecuting
    ? 'border-accent/60'
    : isOnline
      ? 'border-acc-green/60'
      : 'border-acc-red/60';

  const bgTint = isExecuting
    ? 'bg-accent/5'
    : isOnline
      ? 'bg-acc-green/5'
      : 'bg-acc-red/5';

  // Status text color: amber for command failure, accent for executing, green for healthy
  const statusColor = hasFailure
    ? 'text-acc-amber'
    : isExecuting
      ? 'text-accent'
      : isOnline
        ? 'text-acc-green'
        : 'text-acc-red';

  return (
    <div
      className={`rounded-lg p-3 cursor-pointer border transition-all hover:ring-1 hover:ring-accent/40 ${borderColor} ${bgTint}`}
      onClick={() => onSelect(agent.name)}
    >
      <div className="flex items-center gap-2 mb-2">
        <Server size={16} className="text-text-muted shrink-0" />
        <span className="text-sm font-medium text-text-primary truncate flex-1">{agent.name}</span>
        {isOnline
          ? <Wifi size={14} className="text-acc-green shrink-0" />
          : <WifiOff size={14} className="text-acc-red shrink-0" />}
      </div>

      <div className="space-y-1 text-xs">
        <div className="flex justify-between">
          <span className="text-text-muted">Status</span>
          <span className={statusColor}>{agent.status}</span>
        </div>
        <div className="flex justify-between">
          <span className="text-text-muted">Address</span>
          <span className="text-text-secondary truncate max-w-[160px]">{agent.address}</span>
        </div>
        <div className="flex justify-between items-center">
          <span className="text-text-muted">Lock</span>
          {agent.isLocked ? (
            <span className="flex items-center gap-1 text-acc-amber">
              <Lock size={10} /> {agent.lockSource ?? 'locked'}
            </span>
          ) : (
            <span className="flex items-center gap-1 text-acc-green">
              <Unlock size={10} /> free
            </span>
          )}
        </div>
        {agent.isLocked && agent.watchItemTag && (
          <div className="flex justify-between">
            <span className="text-text-muted">Watch Item</span>
            <span className="text-text-secondary truncate max-w-[140px]">{agent.watchItemTag}</span>
          </div>
        )}
        {agent.lastStatusDetail && (
          <div className="flex justify-between">
            <span className="text-text-muted">Detail</span>
            <span className="text-text-secondary truncate max-w-[160px]">{agent.lastStatusDetail}</span>
          </div>
        )}
      </div>
    </div>
  );
});
