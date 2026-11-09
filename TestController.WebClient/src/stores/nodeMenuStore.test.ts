import { describe, it, expect, beforeEach, vi } from 'vitest';

vi.mock('../lib/api', () => ({
  apiFetch: vi.fn(),
  apiErrorMessage: (_e: unknown, fallback: string) => fallback,
}));

import { apiFetch } from '../lib/api';
import { useNodeMenuStore } from './nodeMenuStore';
import type { TreeNode } from '../types/api';

const action = {
  id: 'a', nodeKind: 'Action', displayText: 'Install', tag: 'Install', children: [], isExpanded: false,
  depth: 3, nodePath: 'e0/c1', watchItemTag: 'Sanity',
} as unknown as TreeNode;

describe('nodeMenuStore', () => {
  beforeEach(() => {
    vi.mocked(apiFetch).mockReset();
    useNodeMenuStore.getState().close();
  });

  it('open_Should_LoadTheServerItems_When_TheNodeHasAnAddress', async () => {
    vi.mocked(apiFetch).mockResolvedValue({
      items: [{ id: 'ExecuteAction', label: 'Execute Action', group: 'Run', isEnabled: true }],
    });

    await useNodeMenuStore.getState().open(action, 10, 20);

    expect(vi.mocked(apiFetch).mock.calls[0][0]).toBe('/api/watchlist/node-menu');
    expect(JSON.parse(vi.mocked(apiFetch).mock.calls[0][1]!.body as string))
      .toEqual({ watchItemTag: 'Sanity', nodePath: 'e0/c1' });
    expect(useNodeMenuStore.getState().items.map(i => i.id)).toEqual(['ExecuteAction']);
  });

  it('open_Should_ShowAnErrorAndNoItems_When_TheRequestFails', async () => {
    vi.mocked(apiFetch).mockRejectedValue(new Error('403'));

    await useNodeMenuStore.getState().open(action, 0, 0);

    const s = useNodeMenuStore.getState();
    expect(s.items).toEqual([]);
    expect(s.error).toBe('Could not load the menu');
  });
});
