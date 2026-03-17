import { create } from 'zustand';
import type { WatchListConfig, TreeNode, NodeStatus, WatchItemConfig, EventConfig, ActionNode, TemplateConfig } from '../types/api';

interface WatchListState {
  config: WatchListConfig | null;
  treeRoots: TreeNode[];
  selectedNode: TreeNode | null;
  setConfig: (config: WatchListConfig) => void;
  selectNode: (node: TreeNode | null) => void;
  toggleExpand: (id: string) => void;
  updateNodeStatus: (tag: string, status: NodeStatus) => void;
}

let nodeIdCounter = 0;
function nextId(): string { return `n-${++nodeIdCounter}`; }

function buildActionNodeTree(node: ActionNode, depth: number): TreeNode {
  switch (node.nodeType) {
    case 'ActionGroup': {
      const ag = node;
      return {
        id: nextId(), nodeKind: 'ActionGroup',
        displayText: `[${ag.executionType}] ${ag.tag || 'Group'}`,
        tag: ag.tag, executionStatus: 'Idle',
        children: ag.children.map(c => buildActionNodeTree(c, depth + 1)),
        isExpanded: false, depth, model: node,
      };
    }
    case 'Action': {
      const a = node;
      const label = a.type === 'RunRemoteCommand' && a.agentName
        ? `Remote on '${a.agentName}' — ${a.command}`
        : a.type === 'SendMail' ? `SendMail ? ${a.to}` : a.command || 'Action';
      return {
        id: nextId(), nodeKind: 'Action', displayText: label,
        tag: '', executionStatus: 'Idle', children: [],
        isExpanded: false, depth, model: node,
      };
    }
    case 'Initialize':
      return {
        id: nextId(), nodeKind: 'Initialize',
        displayText: `Initialize: ${node.tag || node.parameterFile}`,
        tag: node.tag, executionStatus: 'Idle', children: [],
        isExpanded: false, depth, model: node,
      };
    case 'Ref':
      return {
        id: nextId(), nodeKind: 'Ref',
        displayText: `Ref ? ${node.templateID}`,
        tag: '', executionStatus: 'Idle', children: [],
        isExpanded: false, depth, model: node,
      };
  }
}

function buildTree(config: WatchListConfig): TreeNode[] {
  nodeIdCounter = 0;
  const watchListRoot: TreeNode = {
    id: nextId(), nodeKind: 'WatchList',
    displayText: `WatchList (${config.watchItems.length} items)`,
    tag: '', executionStatus: 'Idle',
    children: config.watchItems.map((wi: WatchItemConfig) => {
      const wiNode: TreeNode = {
        id: nextId(), nodeKind: 'WatchItem',
        displayText: `${wi.tag || 'Untitled'} (${wi.path}${wi.filter})`,
        tag: wi.tag, executionStatus: 'Idle',
        children: wi.events.map((ev: EventConfig) => {
          const evNode: TreeNode = {
            id: nextId(), nodeKind: 'Event',
            displayText: `Event: ${ev.type} (${ev.executionType})`,
            tag: '', executionStatus: 'Idle',
            children: ev.children.map(c => buildActionNodeTree(c, 3)),
            isExpanded: false, depth: 2, model: ev,
          };
          return evNode;
        }),
        isExpanded: false, depth: 1, model: wi,
      };
      return wiNode;
    }),
    isExpanded: true, depth: 0,
  };

  const roots: TreeNode[] = [watchListRoot];

  if (config.templates.length > 0) {
    const templateRoot: TreeNode = {
      id: nextId(), nodeKind: 'TemplateList',
      displayText: `Templates (${config.templates.length})`,
      tag: '', executionStatus: 'Idle',
      children: config.templates.map((t: TemplateConfig) => ({
        id: nextId(), nodeKind: 'Template' as const,
        displayText: `Template: ${t.id}`,
        tag: t.id, executionStatus: 'Idle' as const,
        children: t.children.map(c => buildActionNodeTree(c, 2)),
        isExpanded: false, depth: 1, model: t,
      })),
      isExpanded: true, depth: 0,
    };
    roots.push(templateRoot);
  }

  return roots;
}

function updateStatusRecursive(nodes: TreeNode[], tag: string, status: NodeStatus): TreeNode[] {
  return nodes.map(n => {
    const updated = { ...n };
    if (n.tag && n.tag.toLowerCase() === tag.toLowerCase()) {
      updated.executionStatus = status;
    }
    if (n.children.length > 0) {
      updated.children = updateStatusRecursive(n.children, tag, status);
    }
    return updated;
  });
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
  selectedNode: null,
  setConfig: (config) => set({ config, treeRoots: buildTree(config) }),
  selectNode: (node) => set({ selectedNode: node }),
  toggleExpand: (id) => set((s) => ({ treeRoots: toggleRecursive(s.treeRoots, id) })),
  updateNodeStatus: (tag, status) =>
    set((s) => ({ treeRoots: updateStatusRecursive(s.treeRoots, tag, status as NodeStatus) })),
}));
