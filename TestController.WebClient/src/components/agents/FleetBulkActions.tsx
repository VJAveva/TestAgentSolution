import { useRef, useState } from 'react';
import { Download, Upload, RotateCw, Trash2, Loader2, AlertTriangle, X } from 'lucide-react';
import { useAgents } from '../../hooks/useAgents';

interface FleetBulkActionsProps {
  /** Agents currently in the fleet (name + address). */
  agents: { name: string; address: string }[];
  /** Called after a bulk register/unregister so the fleet view reloads. */
  onChanged: () => void;
}

type BusyAction = 'import' | 'registerAll' | 'unregisterAll';

interface BulkFeedback {
  kind: 'success' | 'error' | 'info';
  message: string;
  details?: string[];
}

/**
 * Fleet-wide bulk operations: export the agent list to JSON, import a list and
 * register every entry, re-register all listed agents, or unregister them all.
 * Register/unregister run simultaneously via Promise.allSettled in useAgents.
 */
export default function FleetBulkActions({ agents, onChanged }: FleetBulkActionsProps) {
  const { registerMany, unregisterMany } = useAgents();
  const fileInputRef = useRef<HTMLInputElement>(null);
  const [busy, setBusy] = useState<BusyAction | null>(null);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [feedback, setFeedback] = useState<BulkFeedback | null>(null);

  const hasAgents = agents.length > 0;
  const isBusy = busy !== null;

  const report = (verb: string, succeeded: string[], failed: { name: string; error: string }[]) => {
    if (failed.length === 0) {
      setFeedback({ kind: 'success', message: `${verb} ${succeeded.length} agent(s).` });
    } else {
      setFeedback({
        kind: succeeded.length > 0 ? 'info' : 'error',
        message: `${verb} ${succeeded.length} of ${succeeded.length + failed.length} — ${failed.length} failed.`,
        details: failed.map(f => `${f.name}: ${f.error}`),
      });
    }
  };

  // ── Export ──────────────────────────────────────────────────────────
  const handleExport = () => {
    setFeedback(null);
    const payload = {
      exportedUtc: new Date().toISOString(),
      agents: agents.map(a => ({ name: a.name, address: a.address })),
    };
    const blob = new Blob([JSON.stringify(payload, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = `agents-${new Date().toISOString().slice(0, 10)}.json`;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
    URL.revokeObjectURL(url);
    setFeedback({ kind: 'info', message: `Exported ${agents.length} agent(s) to JSON.` });
  };

  // ── Import + register all ───────────────────────────────────────────
  const handleImportClick = () => {
    setFeedback(null);
    fileInputRef.current?.click();
  };

  const handleFileChosen = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    e.target.value = ''; // reset so the same file can be re-selected
    if (!file) return;

    setBusy('import');
    try {
      const list = normalizeImport(JSON.parse(await file.text()));
      if (list.length === 0) {
        setFeedback({
          kind: 'error',
          message: 'No valid agents found. Expected { "agents": [ { "name", "address" } ] }.',
        });
        return;
      }
      const { succeeded, failed } = await registerMany(list);
      onChanged();
      report('Registered', succeeded, failed);
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : 'invalid JSON';
      setFeedback({ kind: 'error', message: `Import failed: ${msg}` });
    } finally {
      setBusy(null);
    }
  };

  // ── Register all (re-register everything currently listed) ──────────
  const handleRegisterAll = async () => {
    if (!hasAgents) return;
    setFeedback(null);
    setBusy('registerAll');
    try {
      const { succeeded, failed } = await registerMany(
        agents.map(a => ({ name: a.name, address: a.address }))
      );
      onChanged();
      report('Registered', succeeded, failed);
    } finally {
      setBusy(null);
    }
  };

  // ── Unregister all (confirmed) ──────────────────────────────────────
  const handleUnregisterAll = async () => {
    setConfirmOpen(false);
    if (!hasAgents) return;
    setFeedback(null);
    setBusy('unregisterAll');
    try {
      const { succeeded, failed } = await unregisterMany(agents.map(a => a.name));
      onChanged();
      report('Unregistered', succeeded, failed);
    } finally {
      setBusy(null);
    }
  };

  const btn =
    'flex items-center gap-1 px-2 py-1 rounded text-xs transition-colors disabled:opacity-40 disabled:cursor-not-allowed';

  return (
    <>
      <div className="flex items-center gap-1">
        <button
          className={`${btn} text-text-muted hover:bg-white/5`}
          onClick={handleExport}
          disabled={isBusy || !hasAgents}
          title="Export the agent list to a JSON file"
        >
          <Download size={12} /> Export
        </button>

        <button
          className={`${btn} text-text-muted hover:bg-white/5`}
          onClick={handleImportClick}
          disabled={isBusy}
          title="Import a JSON agent list and register all entries"
        >
          {busy === 'import' ? <Loader2 size={12} className="animate-spin" /> : <Upload size={12} />} Import
        </button>

        <button
          className={`${btn} text-acc-green hover:bg-acc-green/10`}
          onClick={handleRegisterAll}
          disabled={isBusy || !hasAgents}
          title="Re-register all listed agents simultaneously"
        >
          {busy === 'registerAll' ? <Loader2 size={12} className="animate-spin" /> : <RotateCw size={12} />} Register All
        </button>

        <button
          className={`${btn} text-acc-red hover:bg-acc-red/10`}
          onClick={() => { setFeedback(null); setConfirmOpen(true); }}
          disabled={isBusy || !hasAgents}
          title="Unregister all agents simultaneously"
        >
          {busy === 'unregisterAll' ? <Loader2 size={12} className="animate-spin" /> : <Trash2 size={12} />} Unregister All
        </button>

        <input
          ref={fileInputRef}
          type="file"
          accept="application/json,.json"
          className="hidden"
          onChange={handleFileChosen}
        />
      </div>

      {/* Result banner */}
      {feedback && (
        <div
          className={`flex items-start gap-2 px-4 py-2 text-xs border-b border-bdr ${
            feedback.kind === 'success'
              ? 'bg-acc-green/10 text-acc-green'
              : feedback.kind === 'error'
                ? 'bg-acc-red/10 text-acc-red'
                : 'bg-accent/10 text-accent'
          }`}
        >
          <div className="flex-1">
            <p className="font-medium">{feedback.message}</p>
            {feedback.details && feedback.details.length > 0 && (
              <ul className="mt-1 space-y-0.5 opacity-80">
                {feedback.details.slice(0, 10).map((d, i) => (
                  <li key={i}>• {d}</li>
                ))}
                {feedback.details.length > 10 && <li>• …and {feedback.details.length - 10} more</li>}
              </ul>
            )}
          </div>
          <button
            className="p-0.5 rounded hover:bg-white/10 shrink-0"
            onClick={() => setFeedback(null)}
            title="Dismiss"
          >
            <X size={12} />
          </button>
        </div>
      )}

      {/* Confirm: Unregister All */}
      {confirmOpen && (
        <div className="fixed inset-0 z-50 flex items-center justify-center">
          <div className="absolute inset-0 bg-black/60 backdrop-blur-sm" onClick={() => setConfirmOpen(false)} />
          <div className="relative bg-bg-panel border border-bdr rounded-lg shadow-2xl w-full max-w-md mx-4 p-6">
            <div className="flex items-center gap-3 mb-4">
              <div className="flex-shrink-0 p-2 rounded-full bg-acc-red/10">
                <AlertTriangle size={20} className="text-acc-red" />
              </div>
              <h3 className="text-sm font-bold text-text-primary">Unregister all agents?</h3>
            </div>
            <p className="text-xs text-text-muted mb-4 leading-relaxed">
              This removes all {agents.length} agent(s) from the registry simultaneously. Pipelines using
              these agents will fail until they are re-registered. Export the list first if you want a backup.
            </p>
            <div className="flex justify-end gap-2">
              <button
                className="px-4 py-1.5 text-xs text-text-muted hover:text-text-primary rounded hover:bg-white/5 transition-colors"
                onClick={() => setConfirmOpen(false)}
              >
                Cancel
              </button>
              <button
                className="px-4 py-1.5 text-xs font-semibold rounded transition-colors bg-acc-red/20 text-acc-red hover:bg-acc-red/30"
                onClick={handleUnregisterAll}
              >
                Unregister {agents.length}
              </button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}

/**
 * Accepts either `{ agents: [...] }` or a bare `[...]` array and returns only
 * entries with a non-empty name and address.
 */
function normalizeImport(parsed: unknown): { name: string; address: string }[] {
  const arr: unknown[] = Array.isArray(parsed)
    ? parsed
    : parsed && typeof parsed === 'object' && Array.isArray((parsed as { agents?: unknown }).agents)
      ? ((parsed as { agents: unknown[] }).agents)
      : [];

  const out: { name: string; address: string }[] = [];
  for (const item of arr) {
    if (!item || typeof item !== 'object') continue;
    const rec = item as { name?: unknown; address?: unknown };
    const name = typeof rec.name === 'string' ? rec.name.trim() : '';
    const address = typeof rec.address === 'string' ? rec.address.trim() : '';
    if (name && address) out.push({ name, address });
  }
  return out;
}
