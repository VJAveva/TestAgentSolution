import { create } from 'zustand';
import type { WatchListConfig, TreeNode, NodeStatus, WatchItemConfig, EventConfig, ActionNode, TemplateConfig } from '../types/api';
import { useAuthStore } from './authStore';
import { useSystemModeStore } from './systemModeStore';

interface WatchListState {
  config: WatchListConfig | null;
  treeRoots: TreeNode[];
  nodeStatus: Record<string, NodeStatus>;
  selectedNode: TreeNode | null;
  loading: boolean;
  error: string | null;
  setLoading: (loading: boolean) => void;
  setError: (error: string | null) => void;
  setConfig: (config: WatchListConfig) => void;
  selectNode: (node: TreeNode | null) => void;
  toggleExpand: (id: string) => void;
  updateNodeStatus: (tag: string, status: NodeStatus) => void;
  clearNodeStatus: () => void;
}

let nodeIdCounter = 0;
function nextId(): string { return `n-${++nodeIdCounter}`; }

/**
 * Structural path the server addresses a node by. Mirrors NodeAddressing in Core: the event index
 * then one child index per level. Derived from the same ordered config the server parsed, so the
 * indices agree without any id being persisted in WatchList.xml.
 */
function buildActionNodeTree(
  node: ActionNode,
  depth: number,
  nodePath: string,
  watchItemTag: string | undefined,
): TreeNode {
  const childPath = (i: number) => (nodePath ? `${nodePath}/c${i}` : '');
  switch (node.nodeType) {
    case 'ActionGroup': {
      const ag = node;
      return {
        id: nextId(), nodeKind: 'ActionGroup',
        displayText: ag.tag || 'Group',
        tag: ag.tag, executionStatus: 'Idle',
        children: ag.children.map((c, i) => buildActionNodeTree(c, depth + 1, childPath(i), watchItemTag)),
        isExpanded: false, depth, model: node, nodePath, watchItemTag,
      };
    }
    case 'Action': {
      const a = node;
      const fileName = a.command ? a.command.split(/[/\\]/).pop() || a.command : '';
      const label = a.tag
        ? (a.agentName ? `${a.tag}:${a.agentName}` : a.tag)
        : a.agentName
          ? `${a.agentName} [${fileName}]`
          : fileName || 'Action';
      return {
        id: nextId(), nodeKind: 'Action', displayText: label,
        tag: a.tag || a.order || a.command || '', executionStatus: 'Idle', children: [],
        isExpanded: false, depth, model: node, nodePath, watchItemTag,
      };
    }
    case 'Initialize':
      return {
        id: nextId(), nodeKind: 'Initialize',
        displayText: node.tag || (node.parameterFile ? node.parameterFile.split(/[/\\]/).pop()! : 'Initialize'),
        tag: node.tag, executionStatus: 'Idle', children: [],
        isExpanded: false, depth, model: node, nodePath, watchItemTag,
      };
    case 'Ref':
      return {
        id: nextId(), nodeKind: 'Ref',
        displayText: node.templateID || 'Ref',
        tag: '', executionStatus: 'Idle', children: [],
        isExpanded: false, depth, model: node, nodePath, watchItemTag,
      };
    default:
      return {
        id: nextId(), nodeKind: 'Action',
        displayText: 'Unknown',
        tag: '', executionStatus: 'Idle', children: [],
        isExpanded: false, depth, model: node, nodePath, watchItemTag,
      };
  }
}

function buildTree(config: WatchListConfig): TreeNode[] {
  nodeIdCounter = 0;
  const watchListRoot: TreeNode = {
    id: nextId(), nodeKind: 'WatchList',
    displayText: `Test Plans (${config.watchItems.length} items)`,
    tag: '', executionStatus: 'Idle',
    children: config.watchItems.map((wi: WatchItemConfig) => {
      const wiNode: TreeNode = {
        id: nextId(), nodeKind: 'WatchItem',
        displayText: wi.tag || 'Untitled',
        tag: wi.tag, executionStatus: 'Idle',
        children: wi.events.map((ev: EventConfig, evIndex: number) => {
          const evPath = `e${evIndex}`;
          const evNode: TreeNode = {
            id: nextId(), nodeKind: 'Event',
            displayText: ev.type || 'Event',
            tag: '', executionStatus: 'Idle',
            children: ev.children.map((c, i) => buildActionNodeTree(c, 3, `${evPath}/c${i}`, wi.tag)),
            isExpanded: false, depth: 2, model: ev, nodePath: evPath, watchItemTag: wi.tag,
          };
          return evNode;
        }),
        isExpanded: false, depth: 1, model: wi, watchItemTag: wi.tag,
      };
      return wiNode;
    }),
    isExpanded: true, depth: 0,
  };

  const roots: TreeNode[] = [watchListRoot];

  if (config.templates.length > 0) {
    const templateRoot: TreeNode = {
      id: nextId(), nodeKind: 'TemplateList',
      displayText: `Library (${config.templates.length})`,
      tag: '', executionStatus: 'Idle',
      children: config.templates.map((t: TemplateConfig) => ({
        id: nextId(), nodeKind: 'Template' as const,
        displayText: `Template: ${t.id}`,
        tag: t.id, executionStatus: 'Idle' as const,
        // Template library nodes sit outside any WatchItem, so they carry no runnable node path.
        children: t.children.map(c => buildActionNodeTree(c, 2, '', undefined)),
        isExpanded: false, depth: 1, model: t,
      })),
      isExpanded: true, depth: 0,
    };
    roots.push(templateRoot);
  }

  return roots;
}

function toggleRecursive(nodes: TreeNode[], id: string): TreeNode[] {
  return nodes.map(n => {
    if (n.id === id) return { ...n, isExpanded: !n.isExpanded };
    if (n.children.length > 0) return { ...n, children: toggleRecursive(n.children, id) };
    return n;
  });
}

export const useWatchListStore = create<WatchListState>((set) => ({
  config: null,
  treeRoots: [],
  nodeStatus: {},
  selectedNode: null,
  loading: false,
  error: null,
  setLoading: (loading) => set({ loading }),
  setError: (error) => set({ error }),
  setConfig: (config) => set({ config, treeRoots: buildTree(config), nodeStatus: {}, error: null }),
  selectNode: (node) => set({ selectedNode: node }),
  toggleExpand: (id) => set((s) => ({ treeRoots: toggleRecursive(s.treeRoots, id) })),
  updateNodeStatus: (tag, status) =>
    set((s) => ({ nodeStatus: { ...s.nodeStatus, [tag.toLowerCase()]: status as NodeStatus } })),
  // Called when a run starts so the tree never shows the PREVIOUS run's colours on a re-run.
  clearNodeStatus: () => set({ nodeStatus: {} }),
}));

/**
 * Derived selector: filtered WatchItem nodes based on user role + assignments.
 * Engineers in Secured mode see only assigned pipelines; everyone else sees all.
 * Does NOT store a second copy — derives from existing store state.
 */
export function useFilteredWatchItems(): { items: TreeNode[]; label: string } {
  const treeRoots = useWatchListStore((s) => s.treeRoots);
  const user = useAuthStore((s) => s.user);
  const mode = useSystemModeStore((s) => s.mode);

  const watchListRoot = treeRoots[0];
  if (!watchListRoot) return { items: [], label: '' };

  const allItems = watchListRoot.children;
  const total = allItems.length;

  // Default mode, Admin, SrMgr, Guest, or no user → show all
  if (
    mode === 'default' ||
    !user ||
    user.role === 'Administrator' ||
    user.role === 'SeniorManager' ||
    user.role === 'Guest'
  ) {
    return { items: allItems, label: '' };
  }

  // Engineer in Secured mode: filter by assignment
  const assigned = user.assignedPipelineIds ?? [];
  const filtered = allItems.filter((node) => assigned.includes(node.tag));
  return {
    items: filtered,
    label: `Showing ${filtered.length} of ${total} pipelines`,
  };
}
