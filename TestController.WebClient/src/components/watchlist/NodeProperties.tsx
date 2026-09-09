import { useState, useEffect } from 'react';
import { useWatchListStore } from '../../stores/watchlistStore';
import { apiGet } from '../../lib/api';
import type { WatchItemConfig, ActionConfig, ActionGroupConfig, InitializeConfig, RefConfig, EventConfig } from '../../types/api';

export default function NodeProperties() {
  const node = useWatchListStore(s => s.selectedNode);
  const status = useWatchListStore(s => (node?.tag ? s.nodeStatus[node.tag.toLowerCase()] : undefined) ?? 'Idle');

  if (!node) {
    return (
      <div className="flex flex-col items-center justify-center h-full text-text-muted select-none">
        <img
          src="/nextgen_watermark.png"
          alt=""
          aria-hidden="true"
          className="w-64 h-64 opacity-60 pointer-events-none drop-shadow-sm"
        />
        <p className="text-sm mt-1">Select a node from the tree to view its properties.</p>
      </div>
    );
  }

  return (
    <div className="space-y-3">
      <div className="flex items-center gap-2">
        <span className="text-xs font-bold text-accent uppercase">{node.nodeKind}</span>
        {status !== 'Idle' && (
          <span className={`text-xs px-2 py-0.5 rounded font-medium
            ${status === 'Running' ? 'bg-accent/20 text-accent' :
              status === 'Success' ? 'bg-acc-green/20 text-acc-green' :
              'bg-acc-red/20 text-acc-red'}`}>
            {status}
          </span>
        )}
      </div>
      <h2 className="text-base font-semibold text-text-primary">{node.displayText}</h2>

      {node.nodeKind === 'WatchItem' && node.model && <WatchItemProps model={node.model as WatchItemConfig} />}
      {node.nodeKind === 'Event' && node.model && <EventProps model={node.model as EventConfig} />}
      {node.nodeKind === 'Action' && node.model && <ActionProps model={node.model as ActionConfig} />}
      {node.nodeKind === 'ActionGroup' && node.model && <ActionGroupProps model={node.model as ActionGroupConfig} />}
      {node.nodeKind === 'Initialize' && node.model && <InitializeProps model={node.model as InitializeConfig} />}
      {node.nodeKind === 'Ref' && node.model && <RefProps model={node.model as RefConfig} />}
    </div>
  );
}

function PropRow({ label, value }: { label: string; value: string | number | boolean | undefined }) {
  if (value === undefined || value === '') return null;
  return (
    <div className="flex gap-2 text-xs">
      <span className="text-text-muted w-32 shrink-0">{label}</span>
      <span className="text-text-primary break-all">{String(value)}</span>
    </div>
  );
}

function Card({ children }: { children: React.ReactNode }) {
  return <div className="bg-bg-card rounded-lg p-3 space-y-1.5">{children}</div>;
}

function WatchItemProps({ model }: { model: WatchItemConfig }) {
  // buildNumberField/dropLocationField are parameter KEY NAMES, not values. Resolve the actual
  // build through the API so this shows what the pipeline will run.
  const [build, setBuild] = useState('');
  const [drop, setDrop] = useState('');

  useEffect(() => {
    if (!model.tag) return;
    let cancelled = false;
    apiGet<{ parameters?: Record<string, string> }>(
      `/api/watchlist/${encodeURIComponent(model.tag)}/parameters`)
      .then(data => {
        if (cancelled) return;
        setBuild(data.parameters?.['_BuildNumber'] ?? '');
        setDrop(data.parameters?.['_DropLocation'] ?? '');
      })
      .catch(() => {
        if (cancelled) return;
        setBuild('');
        setDrop('');
      });
    return () => { cancelled = true; };
  }, [model.tag]);

  return (
    <Card>
      <PropRow label="Tag" value={model.tag} />
      <PropRow label="Path" value={model.path} />
      <PropRow label="Filter" value={model.filter} />
      <PropRow label="Enabled" value={model.isEnabled} />
      <PropRow label="Build number" value={build} />
      <PropRow label="Drop location" value={drop} />
      <PropRow label="Build # Field" value={model.buildNumberField} />
      <PropRow label="Drop Field" value={model.dropLocationField} />
      <PropRow label="Events" value={model.events.length} />
    </Card>
  );
}

function EventProps({ model }: { model: EventConfig }) {
  return (
    <Card>
      <PropRow label="Type" value={model.type} />
      <PropRow label="Execution" value={model.executionType} />
      <PropRow label="Children" value={model.children.length} />
    </Card>
  );
}

function ActionProps({ model }: { model: ActionConfig }) {
  return (
    <Card>
      <PropRow label="Tag" value={model.tag} />
      <PropRow label="Type" value={model.type} />
      <PropRow label="Agent" value={model.agentName} />
      <PropRow label="Command" value={model.command} />
      <PropRow label="Parameters" value={model.parameters} />
      <PropRow label="Timeout" value={model.timeout > 0 ? `${model.timeout}s` : 'None'} />
      <PropRow label="FailAndContinue" value={model.failAndContinue} />
      <PropRow label="Skip evaluator" value={model.skip} />
      <PropRow label="Skip reason" value={model.skipReason ?? undefined} />
      <PropRow label="Comment" value={model.comment ?? undefined} />
      <PropRow label="IsReboot" value={model.isReboot} />
      {model.type === 'SendMail' && (
        <>
          <PropRow label="From" value={model.from} />
          <PropRow label="To" value={model.to} />
          <PropRow label="Subject" value={model.title} />
        </>
      )}
    </Card>
  );
}

function ActionGroupProps({ model }: { model: ActionGroupConfig }) {
  return (
    <Card>
      <PropRow label="Tag" value={model.tag} />
      <PropRow label="Execution" value={model.executionType} />
      <PropRow label="FailAndContinue" value={model.failAndContinue} />
      <PropRow label="Skip evaluator" value={model.skip} />
      <PropRow label="Skip reason" value={model.skipReason ?? undefined} />
      <PropRow label="Comment" value={model.comment ?? undefined} />
      <PropRow label="Children" value={model.children.length} />
    </Card>
  );
}

function InitializeProps({ model }: { model: InitializeConfig }) {
  return (
    <Card>
      <PropRow label="Tag" value={model.tag} />
      <PropRow label="Parameter File" value={model.parameterFile} />
    </Card>
  );
}

function RefProps({ model }: { model: RefConfig }) {
  return (
    <Card>
      <PropRow label="Template ID" value={model.templateID} />
    </Card>
  );
}
