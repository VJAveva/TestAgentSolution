import { useState, useEffect } from 'react';
import { X, Play, AlertTriangle } from 'lucide-react';
import { apiGet } from '../../lib/api';
import { useExecution } from '../../hooks/useExecution';
import type { NodeRunScope, PipelineNodeDto, PipelineNodesResponse, TreeNode } from '../../types/api';

interface NodeRunDialogProps {
  node: TreeNode;
  onClose: () => void;
  onRan: () => void;
}

/** Wording per node kind, mirroring the WPF tree's own run affordances. */
const kindCopy: Record<string, { noun: string; only: string; onlyHint: string; withInit: string }> = {
  Event: {
    noun: 'Event',
    only: 'Only this event',
    onlyHint: "Run this event's actions in order. Nothing outside the event runs.",
    withInit: 'This event + its Initialize',
  },
  ActionGroup: {
    noun: 'Action group',
    only: 'Only this group',
    onlyHint: "Run this group's actions in order. Nothing outside the group runs.",
    withInit: 'This group + Initialize',
  },
  Action: {
    noun: 'Single action',
    only: 'Only this action',
    onlyHint: 'Run just this node in isolation — nothing before or after it. Fastest for debugging one step.',
    withInit: 'This action + its Initialize',
  },
  Ref: {
    noun: 'Template action',
    only: 'Only this template',
    onlyHint: "Run this template's actions in order. Nothing outside it runs.",
    withInit: 'This template + Initialize',
  },
};

function findNode(nodes: PipelineNodeDto[], path: string): PipelineNodeDto | null {
  for (const n of nodes) {
    if (n.path === path) return n;
    const hit = findNode(n.children, path);
    if (hit) return hit;
  }
  return null;
}

export default function NodeRunDialog({ node, onClose, onRan }: NodeRunDialogProps) {
  const { runPipelineNode, fetchPipelineNodes } = useExecution();
  const [scope, setScope] = useState<NodeRunScope>('OnlyThisNode');
  const [meta, setMeta] = useState<PipelineNodesResponse | null>(null);
  const [serverNode, setServerNode] = useState<PipelineNodeDto | null>(null);
  const [build, setBuild] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [warning, setWarning] = useState('');

  const tag = node.watchItemTag ?? '';
  const path = node.nodePath ?? '';
  const copy = kindCopy[node.nodeKind] ?? kindCopy.Action;
  const describe = (e: unknown) => (e instanceof Error ? e.message : String(e));

  // The server is the authority on what is runnable and on the current tree revision; the tree's
  // locally-derived path is only a proposal until this confirms it still resolves.
  useEffect(() => {
    let cancelled = false;
    setError('');
    setWarning('');

    fetchPipelineNodes(tag)
      .then(data => {
        if (cancelled) return;
        setMeta(data);
        const found = findNode(data.events, path);
        setServerNode(found);
        if (!found) setError('This node no longer exists in the pipeline. Refresh the tree and try again.');
        else if (!found.runnable) setError('This node cannot be run on its own.');
      })
      .catch(e => !cancelled && setError(`Could not load this pipeline's nodes: ${describe(e)}`));

    apiGet<{ parameters?: Record<string, string> }>(`/api/watchlist/${encodeURIComponent(tag)}/parameters`)
      .then(data => !cancelled && setBuild(data.parameters?.['_BuildNumber'] || ''))
      .catch(e => !cancelled && setWarning(`Could not read the pipeline's build: ${describe(e)}`));

    return () => { cancelled = true; };
  }, [tag, path, fetchPipelineNodes]);

  const run = async () => {
    setLoading(true);
    setError('');
    try {
      await runPipelineNode(tag, { nodePath: path, scope, treeRevision: meta?.treeRevision });
      onRan();
    } catch (e: any) {
      setError(e?.body?.message ?? e?.body?.detail ?? describe(e));
      setLoading(false);
    }
  };

  const canRun = !loading && !!serverNode?.runnable && !!meta;
  const hasInitialize = serverNode?.hasInitialize === true;

  return (
    <div
      className="fixed inset-0 bg-black/60 flex items-center justify-center z-50 p-4"
      role="dialog"
      aria-modal="true"
      aria-label={`Run ${node.displayText}`}
    >
      <div className="bg-bg-card border border-bdr rounded-lg w-full max-w-[520px] max-h-[80vh] overflow-auto shadow-xl">
        <div className="flex items-start justify-between px-4 py-3 border-b border-bdr">
          <div className="min-w-0">
            <h2 className="text-sm font-semibold text-text-primary truncate">Run: {node.displayText}</h2>
            <p className="text-[10px] text-text-muted mt-0.5">
              {copy.noun}
              {serverNode?.agentName ? ` · agent ${serverNode.agentName}` : ''}
              {` · ${tag}`}
            </p>
          </div>
          <button className="p-1 rounded hover:bg-white/10" onClick={onClose} aria-label="Close">
            <X size={14} className="text-text-muted" />
          </button>
        </div>

        <div className="px-4 py-3 space-y-3">
          <fieldset>
            <legend className="text-[10px] uppercase tracking-wide text-text-muted mb-1.5">What to run</legend>

            <label className={`flex gap-2.5 p-2.5 rounded-md border cursor-pointer
              ${scope === 'OnlyThisNode' ? 'border-accent bg-accent/10' : 'border-bdr'}`}>
              <input
                type="radio"
                name="node-run-scope"
                className="mt-0.5"
                checked={scope === 'OnlyThisNode'}
                onChange={() => setScope('OnlyThisNode')}
              />
              <span>
                <span className="block text-xs text-text-primary">{copy.only}</span>
                <span className="block text-[10px] text-text-muted mt-0.5">{copy.onlyHint}</span>
              </span>
            </label>

            {/* Shown only where an ancestor actually declares an Initialize, so the option always means something. */}
            {hasInitialize && (
              <label className={`mt-2 flex gap-2.5 p-2.5 rounded-md border cursor-pointer
                ${scope === 'NodeWithInitialize' ? 'border-accent bg-accent/10' : 'border-bdr'}`}>
                <input
                  type="radio"
                  name="node-run-scope"
                  className="mt-0.5"
                  checked={scope === 'NodeWithInitialize'}
                  onChange={() => setScope('NodeWithInitialize')}
                />
                <span>
                  <span className="block text-xs text-text-primary">{copy.withInit}</span>
                  <span className="block text-[10px] text-text-muted mt-0.5">
                    Load the owning setup step's parameters first, then run. Use if the node needs its parameters loaded.
                  </span>
                </span>
              </label>
            )}
          </fieldset>

          <div>
            <span className="block text-[10px] uppercase tracking-wide text-text-muted mb-1.5">Build</span>
            <div className="rounded-sm border border-bdr bg-bg-input px-2.5 py-2 font-mono text-[11px] text-text-primary">
              {build || '—'} <span className="text-text-muted">(from pipeline)</span>
            </div>
          </div>

          {serverNode && serverNode.children.length > 0 && (
            <div className="rounded-sm bg-white/5 p-2.5">
              <p className="text-[10px] font-medium text-text-muted mb-1">Nodes in this group:</p>
              <p className="font-mono text-[11px] text-text-primary leading-relaxed">
                {serverNode.children.map(c => c.name).join(' → ')}
              </p>
            </div>
          )}

          <p className="flex gap-2 rounded-md border border-acc-yellow/40 bg-acc-yellow/10 px-2.5 py-2 text-[11px] text-text-primary">
            <AlertTriangle size={13} className="shrink-0 mt-0.5 text-acc-yellow" />
            <span>
              This runs on live agents. You have this pipeline assigned, so node-run is allowed.
            </span>
          </p>

          {warning && <p className="text-[11px] text-acc-yellow">{warning}</p>}
          {error && <p className="text-[11px] text-acc-red">{error}</p>}
        </div>

        <div className="flex justify-end gap-2 px-4 py-3 border-t border-bdr bg-white/5">
          <button
            className="px-3 py-1.5 text-xs rounded border border-bdr text-text-secondary hover:bg-white/10"
            onClick={onClose}
          >
            Cancel
          </button>
          <button
            className="inline-flex items-center gap-1.5 px-3 py-1.5 text-xs font-semibold rounded bg-accent text-white
              hover:bg-accent/90 disabled:opacity-50 disabled:cursor-not-allowed"
            disabled={!canRun}
            onClick={run}
          >
            <Play size={12} />
            {loading ? 'Starting…' : `Run ${copy.noun.toLowerCase()}`}
          </button>
        </div>
      </div>
    </div>
  );
}
