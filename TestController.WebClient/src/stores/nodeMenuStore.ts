import { create } from 'zustand';
import { apiFetch, apiErrorMessage } from '../lib/api';
import type { TreeNode } from '../types/api';

/** One item of the server-built node menu (ContextMenu_Requirement.md P8). Ids match NodeMenuIds in Core. */
export interface NodeMenuItemDto {
  id: string;
  label: string;
  group: string;
  isEnabled: boolean;
  toolTip?: string | null;
}

interface NodeMenuState {
  node: TreeNode | null;
  x: number;
  y: number;
  items: NodeMenuItemDto[];
  loading: boolean;
  error: string | null;
  infoTitle: string | null;
  infoText: string | null;
  open: (node: TreeNode, x: number, y: number) => Promise<void>;
  close: () => void;
  showResolvedCommand: (node: TreeNode) => Promise<void>;
  showInfo: (title: string, text: string) => void;
  closeInfo: () => void;
}

/** The pipeline tag and structural path the server addresses a node by; a WatchItem row sends no path. */
export function nodeAddress(node: TreeNode): { watchItemTag: string; nodePath: string | null } | null {
  if (node.nodeKind === 'WatchItem' && node.tag) return { watchItemTag: node.tag, nodePath: null };
  if (node.watchItemTag && node.nodePath) return { watchItemTag: node.watchItemTag, nodePath: node.nodePath };
  return null;
}

export const useNodeMenuStore = create<NodeMenuState>((set, get) => ({
  node: null,
  x: 0,
  y: 0,
  items: [],
  loading: false,
  error: null,
  infoTitle: null,
  infoText: null,

  open: async (node, x, y) => {
    const address = nodeAddress(node);
    if (!address) return;
    set({ node, x, y, items: [], loading: true, error: null });
    try {
      const res = await apiFetch<{ items: NodeMenuItemDto[] }>('/api/watchlist/node-menu', {
        method: 'POST',
        body: JSON.stringify(address),
      });
      if (get().node === node) set({ items: res.items, loading: false });
    } catch (e) {
      if (get().node === node) set({ loading: false, error: apiErrorMessage(e, 'Could not load the menu') });
    }
  },

  close: () => set({ node: null, items: [], loading: false, error: null }),

  showResolvedCommand: async (node) => {
    const address = nodeAddress(node);
    if (!address) return;
    try {
      const res = await apiFetch<{ text: string }>('/api/watchlist/resolved-command', {
        method: 'POST',
        body: JSON.stringify(address),
      });
      set({ infoTitle: `Resolved command - ${node.displayText}`, infoText: res.text });
    } catch (e) {
      set({ infoTitle: 'Resolved command', infoText: apiErrorMessage(e, 'Could not resolve the command') });
    }
  },

  showInfo: (title, text) => set({ infoTitle: title, infoText: text }),
  closeInfo: () => set({ infoTitle: null, infoText: null }),
}));
