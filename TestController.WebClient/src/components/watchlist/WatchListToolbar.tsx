import { useState, useEffect } from 'react';
import { PlayCircle, XCircle, Upload, Download, RefreshCw, RotateCcw } from 'lucide-react';
import { useWatchList } from '../../hooks/useWatchList';
import { useExecution } from '../../hooks/useExecution';
import { useWatchListStore } from '../../stores/watchlistStore';
import { useCan, useDisabledReason } from '../../hooks/useCapabilities';
import { useLockStore } from '../../stores/lockStore';
import TriggerDialog from '../execution/TriggerDialog';
import LockConflictModal from '../dialogs/LockConflictModal';
import type { PipelineLockDto } from '../../stores/lockStore';

export default function WatchListToolbar() {
  const { refresh, importXml, exportXml } = useWatchList();
  const { triggerAll, triggerByTag, cancelAll, retryByTag } = useExecution();
  const selectedNode = useWatchListStore(s => s.selectedNode);
  const [busy, setBusy] = useState(false);
  const [showTriggerDialog, setShowTriggerDialog] = useState(false);
  const [conflictLock, setConflictLock] = useState<PipelineLockDto | null>(null);

  // Retry permission check
  const selectedTag = selectedNode?.nodeKind === 'WatchItem' ? selectedNode.tag : undefined;
  const canRetry = useCan('Pipeline_Retry', selectedTag ?? undefined);
  const retryDeniedReason = useDisabledReason('Pipeline_Retry', selectedTag ?? undefined);
  const isLockedByOther = useLockStore(s => selectedTag ? s.isLockedByOther(selectedTag) : false);
  const selectedStatus = useWatchListStore(s => (selectedTag ? s.nodeStatus[selectedTag.toLowerCase()] : undefined) ?? 'Idle');
  const showRetry = selectedNode?.nodeKind === 'WatchItem' && selectedStatus === 'Failed';

  // Phase 6: detect disabled pipeline from model
  const isPipelineDisabled =
    selectedNode?.nodeKind === 'WatchItem'
    && (selectedNode.model as { isEnabled?: boolean } | undefined)?.isEnabled === false;

  const retryDisabled = busy || !canRetry || isLockedByOther || isPipelineDisabled;

  // Listen for 409 lock conflict events from useExecution
  useEffect(() => {
    const handler = (e: Event) => {
      const lock = (e as CustomEvent).detail?.lock as PipelineLockDto | undefined;
      if (lock) setConflictLock(lock);
    };
    window.addEventListener('pipeline-lock-conflict', handler);
    return () => window.removeEventListener('pipeline-lock-conflict', handler);
  }, []);

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
      setShowTriggerDialog(true);
      return Promise.resolve();
    }
    return triggerAll();
  };

  const handleRetry = async () => {
    if (!selectedTag) return;
    setBusy(true);
    try {
      await retryByTag(selectedTag);
    } catch (e: any) {
      if (e?.response?.status === 403) {
        // Permission denied — already guarded client-side, but catch server 403
        console.warn('Retry denied by server:', e.response.data?.error);
      } else {
        console.error('Retry failed:', e);
      }
    } finally {
      setBusy(false);
    }
  };

  const handleTriggerWithParams = async (buildNumber: string, dropLocation: string, lockVersion?: number) => {
    if (!selectedNode?.tag) return;
    try {
      await triggerByTag(selectedNode.tag, {
        buildNumber: buildNumber || undefined,
        dropLocation: dropLocation || undefined,
        lockVersion,
      });
    } catch (e) {
      console.error('Trigger failed:', e);
    }
    setShowTriggerDialog(false);
  };

  return (
    <div className="flex flex-wrap gap-1 p-2 border-b border-bdr">
      <ToolBtn icon={<PlayCircle size={14} />} label="Trigger" onClick={wrap(handleTrigger)} disabled={busy} accent />
      <ToolBtn icon={<XCircle size={14} />} label="Cancel" onClick={wrap(cancelAll)} disabled={busy} danger />
      <div className="w-px bg-bdr mx-1" />
      <ToolBtn icon={<Upload size={14} />} label="Import" onClick={handleImport} disabled={busy} />
      <ToolBtn icon={<Download size={14} />} label="Export" onClick={wrap(handleExport)} disabled={busy} />
      <ToolBtn icon={<RefreshCw size={14} />} label="Refresh" onClick={wrap(refresh)} disabled={busy} />

      {showRetry && (
        <ToolBtn
          icon={<RotateCcw size={14} />}
          label="Retry Failed"
          onClick={handleRetry}
          disabled={retryDisabled}
          accent
          title={retryDisabled ? (isPipelineDisabled ? 'Pipeline is disabled' : isLockedByOther ? 'Pipeline is locked by another user' : retryDeniedReason ?? undefined) : undefined}
        />
      )}

      {showTriggerDialog && selectedNode?.tag && (
        <TriggerDialog
          watchItemTag={selectedNode.tag}
          isOpen={showTriggerDialog}
          onClose={() => setShowTriggerDialog(false)}
          onTrigger={handleTriggerWithParams}
        />
      )}

      {conflictLock && (
        <LockConflictModal
          open={true}
          lock={conflictLock}
          onClose={() => setConflictLock(null)}
        />
      )}
    </div>
  );
}

function ToolBtn({ icon, label, onClick, disabled, accent, danger, title }: {
  icon: React.ReactNode; label: string; onClick: () => void;
  disabled?: boolean; accent?: boolean; danger?: boolean; title?: string;
}) {
  const base = 'flex items-center gap-1 px-2 py-1 rounded text-xs font-medium transition-colors disabled:opacity-40';
  const color = accent
    ? 'bg-accent/15 text-accent hover:bg-accent/25'
    : danger
      ? 'bg-acc-red/15 text-acc-red hover:bg-acc-red/25'
      : 'bg-white/5 text-text-secondary hover:bg-white/10 hover:text-text-primary';
  return (
    <button className={`${base} ${color}`} onClick={onClick} disabled={disabled} title={title}>
      {icon}{label}
    </button>
  );
}
