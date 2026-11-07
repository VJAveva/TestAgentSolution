import { useEffect, useState } from 'react';
import { apiFetch } from '../../lib/api';
import { useFeatureStore, type FeatureKey } from '../../stores/featureStore';

interface SaveResponse { saved: boolean; message: string; changes: string[] }

/** Admin > Features: switch per-team features on/off. The server re-checks the permission. */
export default function FeatureSettingsPanel() {
  const { teamName, features, fetchFeatures } = useFeatureStore();
  const [draft, setDraft] = useState<Partial<Record<FeatureKey, boolean>>>({});
  const [status, setStatus] = useState<{ text: string; error: boolean } | null>(null);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    setDraft(Object.fromEntries(features.map(f => [f.key, f.configuredOn])) as Partial<Record<FeatureKey, boolean>>);
  }, [features]);

  const save = async () => {
    setSaving(true);
    setStatus(null);
    try {
      const res = await apiFetch<SaveResponse>('/api/features', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ features: draft }),
      });
      setStatus({ text: res.message, error: false });
      await fetchFeatures();
    } catch (err) {
      setStatus({ text: `Save failed: ${err instanceof Error ? err.message : String(err)}`, error: true });
    } finally {
      setSaving(false);
    }
  };

  return (
    <main className="flex-1 overflow-auto p-4">
      <div className="max-w-2xl space-y-3">
        <div>
          <h2 className="text-sm font-semibold text-text-primary">Features on this controller</h2>
          <p className="text-xs text-text-secondary">
            Team: <span className="font-medium">{teamName || '(not set)'}</span>. Pipelines, Agents, Library,
            Pre-flight and Admin are always on.
          </p>
        </div>
        <ul className="divide-y divide-bdr bg-bg-card rounded-lg">
          {features.map(f => (
            <li key={f.key} className="flex items-start gap-3 p-3">
              <input
                id={`feature-${f.key}`}
                type="checkbox"
                className="mt-1"
                checked={draft[f.key] ?? true}
                onChange={e => setDraft(d => ({ ...d, [f.key]: e.target.checked }))}
              />
              <label htmlFor={`feature-${f.key}`} className="text-xs">
                <span className="block font-semibold text-text-primary">{f.key}</span>
                <span className="block text-text-secondary">{f.description}</span>
                {(f.offBecause || f.dependsOn) && (
                  <span className="block text-acc-yellow">{f.offBecause ?? `Depends on ${f.dependsOn}.`}</span>
                )}
              </label>
            </li>
          ))}
        </ul>
        <div className="flex items-center gap-3">
          <button
            onClick={save}
            disabled={saving}
            className="px-3 py-1.5 rounded bg-accent/20 text-accent text-xs font-medium hover:bg-accent/30 disabled:opacity-50"
          >
            {saving ? 'Saving…' : 'Save'}
          </button>
          {status && (
            <span role="status" className={`text-xs ${status.error ? 'text-acc-red' : 'text-text-secondary'}`}>
              {status.text}
            </span>
          )}
        </div>
      </div>
    </main>
  );
}
