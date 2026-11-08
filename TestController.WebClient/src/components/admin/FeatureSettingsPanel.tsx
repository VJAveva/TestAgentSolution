import { useEffect, useState } from 'react';
import { apiFetch } from '../../lib/api';
import { useFeatureStore, type CodeChurnRepository, type FeatureKey } from '../../stores/featureStore';

interface SaveResponse { saved: boolean; message: string; changes: string[] }

interface RepoRow {
  name: string;
  url: string;
  branches: string;
  includePaths: string;
  excludePaths: string;
  credentialRef: string;
}

const toRow = (r: CodeChurnRepository): RepoRow => ({
  name: r.name,
  url: r.url ?? '',
  branches: r.branches.join(', '),
  includePaths: r.includePaths.join(', '),
  excludePaths: r.excludePaths.join(', '),
  credentialRef: r.credentialRef ?? '',
});

const split = (v: string) => v.split(/[,;\n]/).map(s => s.trim()).filter(Boolean);

const toRepo = (r: RepoRow): CodeChurnRepository => ({
  name: r.name.trim(),
  url: r.url.trim() || null,
  branches: split(r.branches),
  includePaths: split(r.includePaths),
  excludePaths: split(r.excludePaths),
  credentialRef: r.credentialRef.trim() || null,
});

const REPO_FIELDS: { key: keyof RepoRow; label: string; placeholder: string }[] = [
  { key: 'name', label: 'Name', placeholder: 'AppServer.Repo' },
  { key: 'url', label: 'Url', placeholder: 'https://dev.azure.com/org/project/_git/repo' },
  { key: 'branches', label: 'Branches', placeholder: 'main, release/2026' },
  { key: 'includePaths', label: 'Include paths', placeholder: 'src/' },
  { key: 'excludePaths', label: 'Exclude paths', placeholder: 'tests/' },
  { key: 'credentialRef', label: 'CredentialRef', placeholder: 'ADO_PAT' },
];

/** Admin > Features: switch per-team features on/off and edit Code Churn repositories. The server re-checks the permission. */
export default function FeatureSettingsPanel() {
  const { teamName, features, codeChurn, fetchFeatures } = useFeatureStore();
  const [draft, setDraft] = useState<Partial<Record<FeatureKey, boolean>>>({});
  const [repos, setRepos] = useState<RepoRow[]>([]);
  const [status, setStatus] = useState<{ text: string; error: boolean } | null>(null);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    setDraft(Object.fromEntries(features.map(f => [f.key, f.configuredOn])) as Partial<Record<FeatureKey, boolean>>);
  }, [features]);

  useEffect(() => {
    setRepos(codeChurn.repositories.map(toRow));
  }, [codeChurn]);

  const reposChanged = () => {
    const next = JSON.stringify(repos.map(toRepo));
    const current = JSON.stringify(codeChurn.repositories.map(r => toRepo(toRow(r))));
    return codeChurn.configured ? next !== current : repos.length > 0;
  };

  const updateRepo = (index: number, key: keyof RepoRow, value: string) =>
    setRepos(rs => rs.map((r, i) => (i === index ? { ...r, [key]: value } : r)));

  const save = async () => {
    setSaving(true);
    setStatus(null);
    const messages: string[] = [];
    try {
      const res = await apiFetch<SaveResponse>('/api/features', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ features: draft }),
      });
      if (res.changes.length > 0) messages.push(res.message);
    } catch (err) {
      setStatus({ text: `Save failed: ${err instanceof Error ? err.message : String(err)}`, error: true });
      setSaving(false);
      return;
    }

    try {
      if (reposChanged()) {
        const res = await apiFetch<SaveResponse>('/api/features/code-churn', {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ repositories: repos.map(toRepo) }),
        });
        messages.push(res.message);
      }
      setStatus({ text: messages.length > 0 ? messages.join(' ') : 'No changes to save.', error: false });
      await fetchFeatures();
    } catch (err) {
      setStatus({
        text: `Features saved, but Code Churn repositories were not: ${err instanceof Error ? err.message : String(err)}`,
        error: true,
      });
    } finally {
      setSaving(false);
    }
  };

  return (
    <main className="flex-1 overflow-auto p-4">
      <div className="max-w-5xl space-y-3">
        <div>
          <h2 className="text-sm font-semibold text-text-primary">Features on this controller</h2>
          <p className="text-xs text-text-secondary">
            Team: <span className="font-medium">{teamName || '(not set)'}</span>. Pipelines, Agents, Library,
            Pre-flight and Admin are always on.
          </p>
        </div>
        <ul className="divide-y divide-bdr bg-bg-card rounded-lg max-w-2xl">
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

        <section aria-label="Code Churn repositories" className="space-y-2">
          <div className="flex items-center justify-between gap-3">
            <div>
              <h3 className="text-sm font-semibold text-text-primary">Code Churn repositories</h3>
              <p className="text-xs text-text-secondary">
                Branches and paths are comma-separated. Paths are folder prefixes (src/) or globs (*.cs); Exclude wins.
                CredentialRef is the name of an environment variable on the controller, never the token.
              </p>
              {!codeChurn.configured && repos.length === 0 && (
                <p className="text-xs text-acc-yellow">
                  Not configured: Code Churn scans every component in the component map. Add a repository to limit it.
                </p>
              )}
              {codeChurn.configured && repos.length === 0 && (
                <p className="text-xs text-acc-yellow">Empty list: Code Churn shows &quot;No repositories configured&quot;.</p>
              )}
            </div>
            <button
              onClick={() => setRepos(rs => [...rs, { name: '', url: '', branches: '', includePaths: '', excludePaths: '', credentialRef: '' }])}
              className="px-3 py-1 rounded bg-bg-card text-text-primary text-xs border border-bdr hover:bg-bg-surface whitespace-nowrap"
            >
              Add repository
            </button>
          </div>
          {repos.length > 0 && (
            <table className="w-full text-xs bg-bg-card rounded-lg">
              <thead>
                <tr className="text-left text-text-secondary">
                  {REPO_FIELDS.map(f => <th key={f.key} className="p-2 font-medium">{f.label}</th>)}
                  <th className="p-2" />
                </tr>
              </thead>
              <tbody>
                {repos.map((r, i) => (
                  <tr key={i} className="border-t border-bdr">
                    {REPO_FIELDS.map(f => (
                      <td key={f.key} className="p-1">
                        <input
                          aria-label={`${f.label} for repository ${i + 1}`}
                          value={r[f.key]}
                          placeholder={f.placeholder}
                          onChange={e => updateRepo(i, f.key, e.target.value)}
                          className="w-full px-2 py-1 rounded bg-bg-surface text-text-primary border border-bdr"
                        />
                      </td>
                    ))}
                    <td className="p-1">
                      <button
                        aria-label={`Remove repository ${i + 1}`}
                        onClick={() => setRepos(rs => rs.filter((_, j) => j !== i))}
                        className="px-2 py-1 text-acc-red hover:underline"
                      >
                        Remove
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </section>

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
