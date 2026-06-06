import { useState, useEffect } from 'react';
import { useWatchListStore } from '../../stores/watchlistStore';
import { apiFetch } from '../../lib/api';
import { logCatch } from '../../lib/logger';
import type { TreeNode, NodeKind } from '../../types/api';
import type { AgentLockInfo } from '../../types/agentWorkspace';
import { ChevronDown, ChevronRight, Eye, Zap, FolderTree, Play, Settings, Link2, FileText, List } from 'lucide-react';

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
  Event:       { label: 'EVT',  cls: 'border-[#A78BFA] text-[#A78BFA] bg-[#A78BFA]/20' },
  ActionGroup: { label: 'SEQ',  cls: 'border-[#38BDF8] text-[#38BDF8] bg-[#38BDF8]/20' },
  Initialize:  { label: 'INIT', cls: 'border-[#2DD4BF] text-[#2DD4BF] bg-[#2DD4BF]/20' },
  Ref:         { label: 'REF',  cls: 'border-[#FBBF24] text-[#FBBF24] bg-[#FBBF24]/20' },
};

/** Determine badge for Action nodes (RMT vs cmd) */
function getActionBadge(node: TreeNode): { label: string; cls: string } | null {
  if (node.nodeKind !== 'Action') return null;
  const model = node.model as { type?: string } | undefined;
  if (model?.type === 'RunRemoteCommand') {
    return { label: 'RMT', cls: 'border-[#FB7185] text-[#FB7185] bg-[#FB7185]/20' };
  }
  return { label: 'cmd', cls: 'border-[#94A3B8] text-[#94A3B8] bg-[#94A3B8]/20' };
}

/** Get PAR badge for parallel action groups */
function getGroupBadge(node: TreeNode): { label: string; cls: string } | null {
  if (node.nodeKind !== 'ActionGroup') return null;
  const model = node.model as { executionType?: string } | undefined;
  if (model?.executionType === 'Parallel') {
    return { label: 'PAR', cls: 'border-[#818CF8] text-[#818CF8] bg-[#818CF8]/20' };
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
  const [locks, setLocks] = useState<AgentLockInfo[]>([]);

  // Load initial lock state
  useEffect(() => {
    apiFetch<{ locks: AgentLockInfo[] }>('/api/execution/locks')
      .then(data => setLocks(data.locks || []))
      .catch(logCatch('WatchListTree', 'fetchLocks'));
  }, []);

  // Subscribe to real-time lock changes
  useEffect(() => {
    const handler = (e: Event) => {
      const detail = (e as CustomEvent).detail;
      setLocks(detail?.locks || []);
    };
    window.addEventListener('agent-locks-changed', handler);
    return () => window.removeEventListener('agent-locks-changed', handler);
  }, []);

  if (loading) {
    return <p className="p-3 text-xs text-text-muted">Loading WatchList…</p>;
  }

  if (error) {
    return <p className="p-3 text-xs text-red-400">{error}</p>;
  }

  if (treeRoots.length === 0) {
    return <p className="p-3 text-xs text-text-muted">No WatchList loaded.</p>;
  }

  return (
    <div className="py-1 select-none">
      {treeRoots.map(root => <TreeNodeRow key={root.id} node={root} locks={locks} />)}
    </div>
  );
}

function TreeNodeRow({ node, locks }: { node: TreeNode; locks: any[] }) {
  const selectNode = useWatchListStore(s => s.selectNode);
  const toggleExpand = useWatchListStore(s => s.toggleExpand);
  const selectedNode = useWatchListStore(s => s.selectedNode);
  const isSelected = selectedNode?.id === node.id;
  const hasChildren = node.children.length > 0;

  return (
    <div>
      <div
        className={`flex items-center gap-1.5 py-1 pr-2 cursor-pointer text-xs transition-colors
          ${isSelected ? 'bg-accent/15 text-accent' : 'hover:bg-white/5 text-text-primary'}`}
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

        {/* Status indicator */}
        {node.executionStatus !== 'Idle' && (
          <span className={`w-2 h-2 rounded-full shrink-0 ${statusDot[node.executionStatus]}`} />
        )}

        {/* Icon + label */}
        {kindIcon[node.nodeKind]}
        <span className="truncate">{node.displayText}</span>

        {/* Action-type badge */}
        <NodeBadge node={node} />

        {/* Child count annotation */}
        {hasChildren && (
          <span className="shrink-0 text-[10px] text-text-muted ml-auto mr-1">
            {node.children.length} {node.children.length === 1 ? 'item' : 'items'}
          </span>
        )}

        {/* Lock indicator for WatchItem nodes */}
        {node.nodeKind === 'WatchItem' && (() => {
          const lockForTag = locks.find(l => l.watchItemTag === node.tag);
          if (lockForTag) {
            return (
              <span className="ml-auto px-2 py-0.5 bg-amber-900/30 text-amber-400 text-[9px] font-bold rounded-full uppercase shrink-0">
                Locked ({lockForTag.userId})
              </span>
            );
          }
          return null;
        })()}
      </div>

      {/* Recursive children */}
      {node.isExpanded && hasChildren && (
        <div>{node.children.map(c => <TreeNodeRow key={c.id} node={c} locks={locks} />)}</div>
      )}
    </div>
  );
}
