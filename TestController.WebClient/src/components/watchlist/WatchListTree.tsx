import { useState, useEffect } from 'react';
import { useWatchListStore, useFilteredWatchItems } from '../../stores/watchlistStore';
import { useLockStore } from '../../stores/lockStore';
import { useAuthStore } from '../../stores/authStore';
import { useSystemModeStore } from '../../stores/systemModeStore';
import { useCan } from '../../hooks/useCapabilities';
import { useExecution } from '../../hooks/useExecution';
import type { TreeNode, NodeKind } from '../../types/api';
import { ChevronDown, ChevronRight, Eye, Zap, FolderTree, Play, Settings, Link2, FileText, List, Circle, Lock } from 'lucide-react';
import type { PipelineLockDto } from '../../stores/lockStore';
import LockBadge from './LockBadge';
import DisabledTriggerButton from '../common/DisabledTriggerButton';
import TriggerDialog from '../execution/TriggerDialog';
import LockConflictModal from '../dialogs/LockConflictModal';

const kindIcon: Record<NodeKind, React.ReactNode> = {
  WatchList:    <List size={14} className="text-accent" />,
  WatchItem:    <Eye size={14} className="text-acc-blue" />,
  Event:        <Zap size={14} className="text-acc-yellow" />,
  ActionGroup:  <FolderTree size={14} className="text-acc-mauve" />,
  Action:       <Play size={14} className="text-acc-green" />,
  Initialize:   <Settings size={14} className="text-text-secondary" />,
  Ref:          <Link2 size={14} className="text-acc-peach" />,
  TemplateList: <List size={14} className="text-acc-mauve" />,
  Template:     <FileText size={14} className="text-acc-mauve" />,
};

/** Action-type badge config: maps NodeKind to badge label + color class */
const kindBadge: Partial<Record<NodeKind, { label: string; cls: string }>> = {
  Event:       { label: 'EVT',  cls: 'border-badge-evt text-badge-evt bg-badge-evt/20' },
  ActionGroup: { label: 'SEQ',  cls: 'border-badge-seq text-badge-seq bg-badge-seq/20' },
  Initialize:  { label: 'INIT', cls: 'border-badge-init text-badge-init bg-badge-init/20' },
  Ref:         { label: 'REF',  cls: 'border-badge-ref text-badge-ref bg-badge-ref/20' },
};

/** Determine badge for Action nodes (RMT vs cmd) */
function getActionBadge(node: TreeNode): { label: string; cls: string } | null {
  if (node.nodeKind !== 'Action') return null;
  const model = node.model as { type?: string } | undefined;
  if (model?.type === 'RunRemoteCommand') {
    return { label: 'RMT', cls: 'border-badge-rmt text-badge-rmt bg-badge-rmt/20' };
  }
  return { label: 'cmd', cls: 'border-badge-cmd text-badge-cmd bg-badge-cmd/20' };
}

/** Get PAR badge for parallel action groups */
function getGroupBadge(node: TreeNode): { label: string; cls: string } | null {
  if (node.nodeKind !== 'ActionGroup') return null;
  const model = node.model as { executionType?: string } | undefined;
  if (model?.executionType === 'Parallel') {
    return { label: 'PAR', cls: 'border-badge-par text-badge-par bg-badge-par/20' };
  }
  return kindBadge.ActionGroup!;
}

function NodeBadge({ node }: { node: TreeNode }) {
  const badge = node.nodeKind === 'Action'
    ? getActionBadge(node)
    : node.nodeKind === 'ActionGroup'
      ? getGroupBadge(node)
      : kindBadge[node.nodeKind] ?? null;

  if (!badge) return null;
  return (
    <span className={`shrink-0 rounded-sm border px-1 py-0 font-mono text-[9px] uppercase leading-tight ${badge.cls}`}>
      {badge.label}
    </span>
  );
}

const statusDot: Record<string, string> = {
  Idle:    '',
  Running: 'bg-accent animate-pulse',
  Success: 'bg-acc-green',
  Failed:  'bg-acc-red',
};

export default function WatchListTree() {
  const treeRoots = useWatchListStore(s => s.treeRoots);
  const loading = useWatchListStore(s => s.loading);
  const error = useWatchListStore(s => s.error);
  // Assignment-aware view: Engineers in Secured mode see only their assigned
  // WatchItems plus a "Showing N of M pipelines" label (mirrors WPF MainViewModel).
  // All other roles/modes get the full list with an empty label.
  const { items: filteredWatchItems, label: filterLabel } = useFilteredWatchItems();
  const { triggerByTag } = useExecution();
  const [triggerTarget, setTriggerTarget] = useState<string | null>(null);
  const [conflictLock, setConflictLock] = useState<PipelineLockDto | null>(null);

  // Listen for 409 lock conflict events from useExecution
  useEffect(() => {
    const handler = (e: Event) => {
      const lock = (e as CustomEvent).detail?.lock as PipelineLockDto | undefined;
      if (lock) setConflictLock(lock);
    };
    window.addEventListener('pipeline-lock-conflict', handler);
    return () => window.removeEventListener('pipeline-lock-conflict', handler);
  }, []);

  const handleTriggerWithParams = async (buildNumber: string, dropLocation: string, lockVersion?: number) => {
    if (!triggerTarget) return;
    try {
      await triggerByTag(triggerTarget, {
        buildNumber: buildNumber || undefined,
        dropLocation: dropLocation || undefined,
        lockVersion,
      });
    } catch (e) {
      console.error('Trigger failed:', e);
    }
    setTriggerTarget(null);
  };

  if (loading) {
    return <p className="p-3 text-xs text-text-muted">Loading WatchList…</p>;
  }

  if (error) {
    return <p className="p-3 text-xs text-red-400">{error}</p>;
  }

  if (treeRoots.length === 0) {
    return <p className="p-3 text-xs text-text-muted">No WatchList loaded.</p>;
  }

  // Rebuild the WatchList root with assignment-filtered children so the existing
  // recursive rendering, expand/collapse, and root annotations keep working while
  // honoring the Engineer's assigned-pipeline visibility. Non-WatchList roots
  // (e.g. Templates) are rendered unchanged.
  const watchListRoot = treeRoots[0];
  const otherRoots = treeRoots.slice(1);
  const filteredWatchListRoot: TreeNode | null = watchListRoot
    ? { ...watchListRoot, children: filteredWatchItems }
    : null;
  const isAssignmentFiltered = filterLabel.length > 0;

  return (
    <div className="py-1 select-none">
      <div className="px-3 pb-2 text-[10px] text-text-muted flex flex-wrap items-center gap-3">
        <span className="inline-flex items-center gap-1"><Circle size={8} className="fill-state-triggerable text-state-triggerable" /> Triggerable</span>
        <span className="inline-flex items-center gap-1"><Circle size={8} className="fill-state-viewonly text-state-viewonly" /> View only</span>
        <span className="inline-flex items-center gap-1"><Circle size={8} className="fill-state-disabled text-state-disabled" /> Disabled</span>
        <span className="inline-flex items-center gap-1"><Lock size={8} className="text-state-locked" /> Locked</span>
        {filterLabel && (
          <span className="ml-auto inline-flex items-center gap-1 text-acc-teal">{filterLabel}</span>
        )}
      </div>

      {filteredWatchListRoot && (
        isAssignmentFiltered && filteredWatchItems.length === 0 ? (
          <p className="px-3 py-2 text-xs text-text-muted">
            No pipelines are assigned to you. Contact an administrator to request access.
          </p>
        ) : (
          <TreeNodeRow key={filteredWatchListRoot.id} node={filteredWatchListRoot} onTriggerRequest={setTriggerTarget} />
        )
      )}

      {otherRoots.map(root => <TreeNodeRow key={root.id} node={root} onTriggerRequest={setTriggerTarget} />)}

      {triggerTarget && (
        <TriggerDialog
          watchItemTag={triggerTarget}
          isOpen={true}
          onClose={() => setTriggerTarget(null)}
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

type PipelineState = 'triggerable' | 'viewOnly' | 'disabled' | 'locked';

function TreeNodeRow({ node, onTriggerRequest }: { node: TreeNode; onTriggerRequest: (tag: string) => void }) {
  const selectNode = useWatchListStore(s => s.selectNode);
  const toggleExpand = useWatchListStore(s => s.toggleExpand);
  const selectedNode = useWatchListStore(s => s.selectedNode);
  const isSelected = selectedNode?.id === node.id;
  const hasChildren = node.children.length > 0;

  // Permission state for WatchItem rows
  const isWatchItem = node.nodeKind === 'WatchItem';
  const isSecured = useSystemModeStore(s => s.isSecured);
  const canTrigger = useCan('Pipeline_Trigger', node.tag ?? undefined);
  const isLockedByOther = useLockStore(s => isWatchItem && node.tag ? s.isLockedByOther(node.tag) : false);
  const lock = useLockStore(s => isWatchItem && node.tag ? s.locks[node.tag] : undefined);
  const currentUserId = useAuthStore(s => s.user?.userId);

  // Phase 6: disabled pipeline detection (only explicit false is disabled)
  const isPipelineDisabled =
    isWatchItem && (node.model as { isEnabled?: boolean } | undefined)?.isEnabled === false;

  // Derive state: locked > disabled > viewOnly > triggerable
  const permissionState: PipelineState =
    isWatchItem
      ? isLockedByOther ? 'locked' : isPipelineDisabled ? 'disabled' : !canTrigger ? 'viewOnly' : 'triggerable'
      : 'triggerable';

  // View-only is intentionally dimmed; disabled stays readable.
  const rowOpacity = permissionState === 'viewOnly' ? 'opacity-[0.65]' : '';
  const isOwnLock = !!lock && lock.ownerUserId === currentUserId;

  return (
    <div>
      <div
        className={`flex items-center gap-1.5 py-1 pr-2 cursor-pointer text-xs transition-colors
          ${isSelected ? 'bg-accent/15 text-accent' : 'hover:bg-white/5 text-text-primary'}
          ${rowOpacity}`}
        style={{ paddingLeft: `${node.depth * 20 + 8}px` }}
        onClick={() => selectNode(node)}
      >
        {/* Expand/collapse toggle */}
        {hasChildren ? (
          <button
            className="p-0.5 hover:bg-white/10 rounded"
            onClick={(e) => { e.stopPropagation(); toggleExpand(node.id); }}
          >
            {node.isExpanded
              ? <ChevronDown size={12} className="text-text-muted" />
              : <ChevronRight size={12} className="text-text-muted" />}
          </button>
        ) : (
          <span className="w-4" />
        )}

        {/* Glanceable trigger state indicator (WatchItem only) */}
        {isWatchItem && <TriggerStateIndicator state={permissionState} />}

        {/* Status indicator */}
        {node.executionStatus !== 'Idle' && (
          <span className={`w-2 h-2 rounded-full shrink-0 ${statusDot[node.executionStatus]}`} />
        )}

        {/* Icon + label */}
        {kindIcon[node.nodeKind]}
        <span className="truncate">{node.displayText}</span>

        {/* Action-type badge */}
        <NodeBadge node={node} />

        {/* Permission pill for WatchItem rows */}
        {isWatchItem && <PermissionPill state={permissionState} isSecured={isSecured} />}

        {/* Per-row trigger affordance with centralized gating */}
        {isWatchItem && node.tag && (
          <div className="shrink-0 ml-1" onClick={(e) => e.stopPropagation()}>
            <DisabledTriggerButton
              pipelineTag={node.tag}
              label="Trigger"
              className="px-2 py-0.5 text-[10px]"
              forceDisabled={isPipelineDisabled}
              forceReason="This pipeline is disabled"
              onTrigger={() => onTriggerRequest(node.tag!)}
            />
          </div>
        )}

        {/* Child count annotation */}
        {hasChildren && (
          <span className="shrink-0 text-[10px] text-text-muted ml-auto mr-1">
            {node.children.length} {node.children.length === 1 ? 'item' : 'items'}
          </span>
        )}

        {/* Pipeline lock badge for WatchItem nodes */}
        {isWatchItem && lock && <LockBadge lock={lock} isOwn={isOwnLock} />}
      </div>

      {/* Recursive children */}
      {node.isExpanded && hasChildren && (
        <div>{node.children.map(c => <TreeNodeRow key={c.id} node={c} onTriggerRequest={onTriggerRequest} />)}</div>
      )}
    </div>
  );
}

function TriggerStateIndicator({ state }: { state: PipelineState }) {
  if (state === 'triggerable') {
    return (
      <span className="shrink-0" title="Triggerable">
        <Circle size={8} className="fill-state-triggerable text-state-triggerable" />
      </span>
    );
  }
  if (state === 'viewOnly') {
    return (
      <span className="shrink-0" title="View only">
        <Circle size={8} className="fill-state-viewonly text-state-viewonly" />
      </span>
    );
  }
  if (state === 'disabled') {
    return (
      <span className="shrink-0" title="Disabled">
        <Circle size={8} className="fill-state-disabled text-state-disabled" />
      </span>
    );
  }
  return (
    <span className="shrink-0" title="Locked">
      <Lock size={8} className="text-state-locked" />
    </span>
  );
}

/** Permission pill matching WPF visual language */
function PermissionPill({ state, isSecured }: { state: PipelineState; isSecured: boolean }) {
  // Disabled pill: admin-disabled in config (read-only in web)
  if (state === 'disabled') {
    return (
      <span className="inline-flex items-center gap-0.5 px-1.5 py-0 rounded text-[9px] font-medium shrink-0 border border-state-disabled/60 bg-state-disabled/10 text-state-disabled">
        Disabled
      </span>
    );
  }

  // Triggerable pill: only show in Secured mode (in Default mode everyone is viewOnly, no "assigned" concept)
  if (state === 'triggerable' && isSecured) {
    return (
      <span className="inline-flex items-center gap-0.5 px-1.5 py-0 rounded text-[9px] font-medium shrink-0 bg-state-triggerable/15 text-state-triggerable">
        Assigned to you
      </span>
    );
  }

  // ViewOnly pill: grey with eye icon
  if (state === 'viewOnly') {
    return (
      <span className="inline-flex items-center gap-0.5 px-1.5 py-0 rounded text-[9px] font-medium shrink-0 bg-state-viewonly/10 text-state-viewonly">
        <Eye size={9} />
        View only
      </span>
    );
  }

  // Locked state: handled by LockBadge.
  return null;
}
