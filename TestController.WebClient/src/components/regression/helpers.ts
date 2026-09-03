import type { SubsystemRow, RegressionChangeRef, RegressionWorkItemRef } from '../../types/api';

// Client-side mirror of RegressionViewModel.ApplyFilter + SubsystemRowViewModel projections (WPF parity).

export interface FilterState {
  showRuntime: boolean;
  showConfig: boolean;
  hideAutomated: boolean;
  showAllChanges: boolean;
  filterBug: boolean;
  filterStory: boolean;
  filterIms: boolean;
  component: string | null;
}

function isPackageNoiseChange(c: RegressionChangeRef): boolean {
  return (c.summary ?? '').toLowerCase().includes('universal-package');
}

function stripPackageNoise(row: SubsystemRow): SubsystemRow {
  const kept = row.changes.filter((c) => !isPackageNoiseChange(c));
  return kept.length === row.changes.length ? row : { ...row, changes: kept };
}

function stripAutomated(row: SubsystemRow): SubsystemRow {
  const human = row.changes.filter((c) => c.kind !== 'Automated');
  if (human.length === row.changes.length) return row;
  const files = Array.from(new Set(human.flatMap((c) => c.filePaths)));
  return { ...row, changes: human, filesModified: files.slice(0, 25), totalFilesModified: files.length };
}

export function applyFilters(rows: SubsystemRow[], f: FilterState): SubsystemRow[] {
  let out = rows.filter(
    (r) =>
      r.category === 'Unclassified' ||
      r.category === 'Both' ||
      (r.category === 'Runtime' && f.showRuntime) ||
      (r.category === 'Config' && f.showConfig),
  );
  if (f.component) out = out.filter((r) => r.component === f.component);
  if (f.hideAutomated) out = out.map(stripAutomated).filter((r) => r.changes.length > 0);
  // Hide Universal-Packages manifest bumps by default; "All changes" restores them.
  if (!f.showAllChanges) out = out.map(stripPackageNoise).filter((r) => r.changes.length > 0);
  if (f.filterBug || f.filterStory || f.filterIms) {
    out = out.filter((r) => {
      const wis = allWorkItems(r);
      return (
        (f.filterBug && wis.some((w) => w.kind === 'Bug')) ||
        (f.filterStory && wis.some((w) => w.kind === 'Story')) ||
        (f.filterIms && wis.some((w) => w.kind === 'Ims'))
      );
    });
  }
  return out;
}

export function allWorkItems(row: SubsystemRow): RegressionWorkItemRef[] {
  const map = new Map<number, RegressionWorkItemRef>();
  for (const c of row.changes) for (const w of c.workItems) if (!map.has(w.id)) map.set(w.id, w);
  return Array.from(map.values());
}

export function sortedChanges(row: SubsystemRow): RegressionChangeRef[] {
  return [...row.changes].sort((a, b) => (a.observedUtc < b.observedUtc ? -1 : a.observedUtc > b.observedUtc ? 1 : 0));
}

export interface WorkItemGroup {
  label: string;
  items: RegressionWorkItemRef[];
}

export function workItemGroups(row: SubsystemRow): WorkItemGroup[] {
  const all = allWorkItems(row);
  const byCreated = (a: RegressionWorkItemRef, b: RegressionWorkItemRef) => {
    const av = a.createdUtc ?? '';
    const bv = b.createdUtc ?? '';
    return av > bv ? -1 : av < bv ? 1 : 0;
  };
  const groups: WorkItemGroup[] = [];
  const add = (label: string, pred: (w: RegressionWorkItemRef) => boolean) => {
    const items = all.filter(pred).sort(byCreated);
    if (items.length) groups.push({ label, items });
  };
  add('User Stories', (w) => w.kind === 'Story');
  add('Bug', (w) => w.kind === 'Bug');
  add('IMS', (w) => w.kind === 'Ims');
  add('Others', (w) => w.kind !== 'Story' && w.kind !== 'Bug' && w.kind !== 'Ims');
  return groups;
}

export interface FileLink {
  path: string;
  url: string | null;
}

export function modifiedFileLinks(row: SubsystemRow): FileLink[] {
  const byPath = new Map<string, string | null>(); // path -> commit id
  for (const c of row.changes) for (const p of c.filePaths) if (p && !byPath.has(p)) byPath.set(p, c.changeId);
  for (const p of row.filesModified) if (p && !byPath.has(p)) byPath.set(p, null);
  return Array.from(byPath.entries())
    .filter(([path]) => isSourceFile(path)) // source files only (.h/.cpp/.cs), matching the churn Excel
    .slice(0, 50)
    .map(([path, commit]) => ({ path, url: fileUrl(row, path, commit) }));
}

function isSourceFile(path: string): boolean {
  const p = path.toLowerCase();
  return p.endsWith('.h') || p.endsWith('.cpp') || p.endsWith('.cs');
}

function fileUrl(row: SubsystemRow, path: string, commitId: string | null): string | null {
  if (!row.repositoryUrl) return null;
  let url = `${row.repositoryUrl}?path=${encodeURIComponent(path)}`;
  if (commitId) url += `&version=GC${commitId}`;
  else if (row.defaultBranch) url += `&version=GB${encodeURIComponent(row.defaultBranch)}`;
  return url;
}

export function functionalTests(row: SubsystemRow): string[] {
  const set = new Set<string>();
  for (const u of row.useCases ?? []) if (u.trim()) set.add(u);
  for (const a of row.regressionAreas ?? []) if (a.trim()) set.add(a);
  return Array.from(set);
}

export function subsystemsText(row: SubsystemRow): string {
  return row.solutionNames && row.solutionNames.length > 0 ? row.solutionNames.join(', ') : '—';
}

export function changeKindLabel(row: SubsystemRow): string {
  const pr = row.changes.filter((c) => c.kind === 'PullRequest').length;
  const commit = row.changes.filter((c) => c.kind === 'Commit').length;
  const auto = row.changes.filter((c) => c.kind === 'Automated').length;
  const parts: string[] = [];
  if (pr) parts.push(`${pr} PR`);
  if (commit) parts.push(`${commit} commit`);
  if (auto) parts.push(`${auto} auto`);
  return parts.join(' · ');
}

export function summarizeComponent(row: SubsystemRow): string {
  const pr = row.changes.filter((c) => c.kind === 'PullRequest').length;
  const commit = row.changes.filter((c) => c.kind === 'Commit').length;
  const auto = row.changes.filter((c) => c.kind === 'Automated').length;
  const bugs = allWorkItems(row).filter((w) => w.kind === 'Bug').length;
  const mix: string[] = [];
  if (pr) mix.push(`${pr} PR`);
  if (commit) mix.push(`${commit} commit`);
  if (auto) mix.push(`${auto} auto`);
  const parts: string[] = [
    `${row.component} (${row.category}) has ${row.changes.length} change(s)${mix.length ? ` [${mix.join(', ')}]` : ''} touching ${row.totalFilesModified} file(s).`,
  ];
  if (bugs) parts.push(`${bugs} bug(s) linked — prioritise these.`);
  parts.push(
    (row.riskTier ?? '').toLowerCase() === 'succeeded'
      ? 'Latest build succeeded.'
      : `Latest build: ${row.riskTier} — verify before testing.`,
  );
  const tests = functionalTests(row);
  parts.push(
    tests.length ? `Run functional tests: ${tests.join(', ')}.` : 'No functional test areas mapped for this component yet.',
  );
  return parts.join(' ');
}
