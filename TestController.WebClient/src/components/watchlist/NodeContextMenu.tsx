import { useEffect } from 'react';
import { useNodeMenuStore, type NodeMenuItemDto } from '../../stores/nodeMenuStore';
import { apiFetch, apiErrorMessage } from '../../lib/api';
import { useExecution } from '../../hooks/useExecution';
import type { TreeNode } from '../../types/api';

/**
 * Right-click menu for a WatchList row (ContextMenu_Requirement.md P8, read-only v1). The server decides the
 * items and their enabled state from the same NodeMenuProvider as WPF; each action calls an endpoint that
 * repeats the permission check. No editing items on the web yet.
 */
export default function NodeContextMenu({ onTriggerRequest, onNodeRunRequest }: {
  onTriggerRequest: (tag: string) => void;
  onNodeRunRequest: (node: TreeNode) => void;
}) {
  const { node, x, y, items, loading, error, close, showResolvedCommand, showInfo, infoTitle, infoText, closeInfo } =
    useNodeMenuStore();
  const { cancelSession, fetchSessions } = useExecution();

  useEffect(() => {
    if (!node) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') close(); };
    window.addEventListener('keydown', onKey);
    window.addEventListener('click', close);
    return () => { window.removeEventListener('keydown', onKey); window.removeEventListener('click', close); };
  }, [node, close]);

  const tagOf = (n: TreeNode) => (n.nodeKind === 'WatchItem' ? n.tag : n.watchItemTag) ?? '';

  async function run(item: NodeMenuItemDto) {
    if (!node || !item.isEnabled) return;
    const target = node;
    close();
    const tag = tagOf(target);
    switch (item.id) {
      case 'TriggerAllEvents':
        onTriggerRequest(tag);
        return;
      case 'TriggerEvent':
      case 'ExecuteGroup':
      case 'ExecuteAction':
        onNodeRunRequest(target);
        return;
      case 'ShowResolvedCommand':
        await showResolvedCommand(target);
        return;
      case 'CheckOnly':
        try {
          const report = await apiFetch<{ text: string }>(`/api/preflight/${encodeURIComponent(tag)}`, { method: 'POST' });
          showInfo(`Pre-flight - ${tag}`, report.text);
        } catch (e) {
          showInfo(`Pre-flight - ${tag}`, apiErrorMessage(e, 'Pre-flight failed to run'));
        }
        return;
      case 'Stop': {
        if (!confirm(`Stop the running pipeline "${tag}"?`)) return;
        const data = await fetchSessions();
        const session = data.sessions?.find(s => s.watchItemTag === tag);
        if (session) await cancelSession(session.sessionId);
        return;
      }
    }
  }

  return (
    <>
      {node && (
        <div
          role="menu"
          className="fixed z-50 min-w-[12rem] rounded border border-bdr bg-bg-secondary py-1 text-xs shadow-lg"
          style={{ left: x, top: y }}
          onClick={e => e.stopPropagation()}
        >
          {loading && <p className="px-3 py-1 text-text-muted">Loading…</p>}
          {error && <p className="px-3 py-1 text-acc-red">{error}</p>}
          {!loading && !error && items.length === 0 && <p className="px-3 py-1 text-text-muted">No actions</p>}
          {items.map((item, i) => (
            <div key={item.id}>
              {i > 0 && items[i - 1].group !== item.group && <div className="my-1 border-t border-bdr" />}
              <button
                role="menuitem"
                className="block w-full px-3 py-1 text-left hover:bg-white/5 disabled:cursor-not-allowed disabled:opacity-50"
                disabled={!item.isEnabled}
                title={item.toolTip ?? undefined}
                onClick={() => run(item)}
              >
                {item.label}
              </button>
            </div>
          ))}
        </div>
      )}

      {infoTitle && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50" onClick={closeInfo}>
          <div
            role="dialog"
            aria-label={infoTitle}
            className="max-h-[80vh] w-[40rem] max-w-[90vw] overflow-auto rounded border border-bdr bg-bg-secondary p-4 text-xs"
            onClick={e => e.stopPropagation()}
          >
            <h3 className="mb-2 font-semibold">{infoTitle}</h3>
            <pre className="whitespace-pre-wrap break-words font-mono">{infoText}</pre>
            <div className="mt-3 text-right">
              <button className="rounded border border-bdr px-3 py-1 hover:bg-white/5" onClick={closeInfo}>Close</button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}
