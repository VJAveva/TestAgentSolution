import { useWatchListStore } from '../../stores/watchlistStore';
import type { TreeNode, NodeKind } from '../../types/api';
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
      {treeRoots.map(root => <TreeNodeRow key={root.id} node={root} />)}
    </div>
  );
}

function TreeNodeRow({ node }: { node: TreeNode }) {
  const selectNode = useWatchListStore(s => s.selectNode);
  const toggleExpand = useWatchListStore(s => s.toggleExpand);
  const selectedNode = useWatchListStore(s => s.selectedNode);
  const isSelected = selectedNode?.id === node.id;
  const hasChildren = node.children.length > 0;

  return (
    <div>
      <div
        className={`flex items-center gap-1 py-0.5 pr-2 cursor-pointer text-xs transition-colors
          ${isSelected ? 'bg-accent/15 text-accent' : 'hover:bg-white/5 text-text-primary'}`}
        style={{ paddingLeft: `${node.depth * 16 + 8}px` }}
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
      </div>

      {/* Recursive children */}
      {node.isExpanded && hasChildren && (
        <div>{node.children.map(c => <TreeNodeRow key={c.id} node={c} />)}</div>
      )}
    </div>
  );
}
