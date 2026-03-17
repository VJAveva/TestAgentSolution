import { useState } from 'react';
import { PlayCircle, XCircle, Upload, Download, RefreshCw } from 'lucide-react';
import { useWatchList } from '../../hooks/useWatchList';
import { useExecution } from '../../hooks/useExecution';
import { useWatchListStore } from '../../stores/watchlistStore';

export default function WatchListToolbar() {
  const { refresh, importXml, exportXml } = useWatchList();
  const { triggerAll, triggerByTag, cancelAll } = useExecution();
  const selectedNode = useWatchListStore(s => s.selectedNode);
  const [busy, setBusy] = useState(false);

  const wrap = (fn: () => Promise<unknown>) => async () => {
    setBusy(true);
    try { await fn(); } catch (e) { console.error(e); }
    finally { setBusy(false); }
  };

  const handleImport = async () => {
    const input = document.createElement('input');
    input.type = 'file';
    input.accept = '.xml';
    input.onchange = async () => {
      const file = input.files?.[0];
      if (!file) return;
      const xml = await file.text();
      await importXml(xml);
    };
    input.click();
  };

  const handleExport = async () => {
    const tags = selectedNode?.nodeKind === 'WatchItem' && selectedNode.tag ? [selectedNode.tag] : undefined;
    const xml = await exportXml(tags);
    const blob = new Blob([xml], { type: 'application/xml' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = tags ? `WatchItem_${tags[0]}.xml` : 'WatchItems_All.xml';
    a.click();
    URL.revokeObjectURL(url);
  };

  const handleTrigger = () => {
    if (selectedNode?.nodeKind === 'WatchItem' && selectedNode.tag) {
      return triggerByTag(selectedNode.tag);
    }
    return triggerAll();
  };

  return (
    <div className="flex flex-wrap gap-1 p-2 border-b border-bdr">
      <ToolBtn icon={<PlayCircle size={14} />} label="Trigger" onClick={wrap(handleTrigger)} disabled={busy} accent />
      <ToolBtn icon={<XCircle size={14} />} label="Cancel" onClick={wrap(cancelAll)} disabled={busy} danger />
      <div className="w-px bg-bdr mx-1" />
      <ToolBtn icon={<Upload size={14} />} label="Import" onClick={handleImport} disabled={busy} />
      <ToolBtn icon={<Download size={14} />} label="Export" onClick={wrap(handleExport)} disabled={busy} />
      <ToolBtn icon={<RefreshCw size={14} />} label="Refresh" onClick={wrap(refresh)} disabled={busy} />
    </div>
  );
}

function ToolBtn({ icon, label, onClick, disabled, accent, danger }: {
  icon: React.ReactNode; label: string; onClick: () => void;
  disabled?: boolean; accent?: boolean; danger?: boolean;
}) {
  const base = 'flex items-center gap-1 px-2 py-1 rounded text-xs font-medium transition-colors disabled:opacity-40';
  const color = accent
    ? 'bg-accent/15 text-accent hover:bg-accent/25'
    : danger
      ? 'bg-acc-red/15 text-acc-red hover:bg-acc-red/25'
      : 'bg-white/5 text-text-secondary hover:bg-white/10 hover:text-text-primary';
  return (
    <button className={`${base} ${color}`} onClick={onClick} disabled={disabled}>
      {icon}{label}
    </button>
  );
}
