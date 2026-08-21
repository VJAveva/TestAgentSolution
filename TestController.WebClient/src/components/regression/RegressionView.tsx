import { useEffect, useMemo, useState } from 'react';
import { AlertTriangle, RotateCw, ChevronRight, ChevronDown, Download, Mail, Brain } from 'lucide-react';
import { useRegression } from '../../hooks/useRegression';
import { useRegressionStore } from '../../stores/regressionStore';
import type { RegressionBuildRef, RegressionCategoryKind, RegressionScopeKind, SubsystemRow } from '../../types/api';
import {
  applyFilters,
  changeKindLabel,
  functionalTests,
  modifiedFileLinks,
  sortedChanges,
  subsystemsText,
  summarizeComponent,
  workItemGroups,
} from './helpers';

const ALL_BRANCHES = '(all branches)';
const ALL_COMPONENTS = '(all components)';
const COL_SPAN = 13;

function categoryBadgeClass(cat: RegressionCategoryKind): string {
  switch (cat) {
    case 'Runtime': return 'bg-acc-green/20 text-acc-green';
    case 'Config': return 'bg-acc-blue/20 text-acc-blue';
    case 'Both': return 'bg-acc-amber/20 text-acc-amber';
    case 'Unclassified': return 'bg-acc-red/20 text-acc-red';
  }
}

function rowBorderClass(cat: RegressionCategoryKind): string {
  switch (cat) {
    case 'Runtime': return 'border-l-acc-green';
    case 'Config': return 'border-l-acc-blue';
    case 'Both': return 'border-l-acc-amber';
    case 'Unclassified': return 'border-l-acc-red';
  }
}

function estimateText(row: SubsystemRow): string {
  const val = Math.round(row.estimatedMinutes);
  return row.isEstimate ? `~${val} min (est.)` : `${val} min`;
}

function toDateInput(d: Date): string {
  return d.toISOString().slice(0, 10);
}

function Toggle({ label, active, onClick, activeClass }: { label: string; active: boolean; onClick: () => void; activeClass?: string }) {
  return (
    <button
      onClick={onClick}
      className={`px-2.5 py-1 rounded text-xs font-medium border transition-colors ${
        active ? activeClass ?? 'bg-accent/20 border-accent text-accent' : 'border-bdr text-text-secondary hover:bg-bg-surface'
      }`}
    >
      {label}
    </button>
  );
}

export default function RegressionView() {
  const { loadAll, fetchBranches, fetchComponents, fetchBuilds, fetchBuildImpact, downloadReport, emailReport } = useRegression();
  const consolidated = useRegressionStore((s) => s.consolidated);
  const scope = useRegressionStore((s) => s.scope);
  const syncStatus = useRegressionStore((s) => s.syncStatus);
  const connection = useRegressionStore((s) => s.connection);
  const summary = useRegressionStore((s) => s.summary);
  const branches = useRegressionStore((s) => s.branches);
  const components = useRegressionStore((s) => s.components);
  const showRuntime = useRegressionStore((s) => s.showRuntime);
  const showConfig = useRegressionStore((s) => s.showConfig);
  const hideAutomated = useRegressionStore((s) => s.hideAutomated);
  const filterBug = useRegressionStore((s) => s.filterBug);
  const filterStory = useRegressionStore((s) => s.filterStory);
  const filterIms = useRegressionStore((s) => s.filterIms);
  const setShowRuntime = useRegressionStore((s) => s.setShowRuntime);
  const setShowConfig = useRegressionStore((s) => s.setShowConfig);
  const setHideAutomated = useRegressionStore((s) => s.setHideAutomated);
  const setFilterBug = useRegressionStore((s) => s.setFilterBug);
  const setFilterStory = useRegressionStore((s) => s.setFilterStory);
  const setFilterIms = useRegressionStore((s) => s.setFilterIms);
  const loading = useRegressionStore((s) => s.loading);
  const error = useRegressionStore((s) => s.error);

  const [scopeKind, setScopeKind] = useState<RegressionScopeKind>('Weekly');
  const [from, setFrom] = useState(() => {
    const d = new Date();
    d.setDate(d.getDate() - 7);
    return d;
  });
  const [to, setTo] = useState(() => new Date());
  const [branch, setBranch] = useState<string | null>(null);
  const [selectedDefId, setSelectedDefId] = useState<number | null>(null);
  const [selectedComponentName, setSelectedComponentName] = useState<string | null>(null);
  const [inspectBuilds, setInspectBuilds] = useState<RegressionBuildRef[]>([]);
  const [selectedBuildId, setSelectedBuildId] = useState<number | null>(null);
  const [focusedRow, setFocusedRow] = useState<SubsystemRow | null>(null);
  const [recipients, setRecipients] = useState('');
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [summaryOpen, setSummaryOpen] = useState(false);
  const [statusMessage, setStatusMessage] = useState('');

  useEffect(() => {
    loadAll(from, to, null);
    fetchBranches();
    fetchComponents();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const reload = (f: Date, t: Date, b: string | null) => {
    setFocusedRow(null);
    setSelectedBuildId(null);
    loadAll(f, t, b);
  };

  const selectScope = (kind: RegressionScopeKind) => {
    setScopeKind(kind);
    const today = new Date();
    let newFrom = from;
    const newTo = today;
    if (kind === 'Build') newFrom = today;
    else if (kind === 'Weekly') { newFrom = new Date(today); newFrom.setDate(today.getDate() - 7); }
    else if (kind === 'Release') { newFrom = new Date(today); newFrom.setDate(today.getDate() - 45); }
    setFrom(newFrom);
    setTo(newTo);
    if (kind !== 'Custom') reload(newFrom, newTo, branch);
  };

  const applyRange = () => {
    setScopeKind('Custom');
    reload(from, to, branch);
  };

  const changeBranch = (value: string) => {
    const b = value === ALL_BRANCHES ? null : value;
    setBranch(b);
    reload(from, to, b);
  };

  const onComponentPick = async (defIdStr: string) => {
    const defId = Number(defIdStr);
    setSelectedBuildId(null);
    setFocusedRow(null);
    if (!defId) {
      setSelectedDefId(null);
      setSelectedComponentName(null);
      setInspectBuilds([]);
      return;
    }
    const comp = components.find((c) => c.definitionId === defId);
    setSelectedDefId(defId);
    setSelectedComponentName(comp?.name ?? null);
    try {
      setInspectBuilds(await fetchBuilds(defId));
    } catch {
      setInspectBuilds([]);
    }
  };

  const onBuildPick = async (buildIdStr: string) => {
    const buildId = Number(buildIdStr);
    setSelectedBuildId(buildId || null);
    if (!buildId || !selectedDefId) {
      setFocusedRow(null);
      return;
    }
    try {
      setStatusMessage('Loading build…');
      const row = await fetchBuildImpact(selectedDefId, buildId);
      setFocusedRow(row);
      setStatusMessage(`${row.component} build ${row.buildNumber ?? ''}: ${row.changes.length} change(s).`);
    } catch (err) {
      setFocusedRow(null);
      setStatusMessage(`Load failed: ${err instanceof Error ? err.message : err}`);
    }
  };

  const clearFocus = () => {
    setFocusedRow(null);
    setSelectedBuildId(null);
  };

  const runExport = async (format: 'csv' | 'html') => {
    try {
      setStatusMessage(`Generating ${format.toUpperCase()} report…`);
      await downloadReport(from, to, branch, format);
      setStatusMessage(`${format.toUpperCase()} report downloaded.`);
    } catch (e) {
      setStatusMessage(`Export failed: ${e instanceof Error ? e.message : e}`);
    }
  };

  const sendEmail = async () => {
    if (!recipients.trim()) { setStatusMessage('Enter at least one recipient email.'); return; }
    try {
      setStatusMessage(`Emailing report to ${recipients}…`);
      await emailReport(from, to, branch, recipients);
      setStatusMessage(`Report emailed to ${recipients}.`);
    } catch (e) {
      setStatusMessage(`Email failed: ${e instanceof Error ? e.message : e}`);
    }
  };

  const rows = useMemo(
    () =>
      applyFilters(consolidated?.rows ?? [], {
        showRuntime, showConfig, hideAutomated, filterBug, filterStory, filterIms, component: selectedComponentName,
      }),
    [consolidated, showRuntime, showConfig, hideAutomated, filterBug, filterStory, filterIms, selectedComponentName],
  );

  const latestBuild = useMemo(() => {
    const withBuild = (consolidated?.rows ?? []).filter((r) => r.buildNumber);
    withBuild.sort((a, b) => ((b.buildFinishedUtc ?? '') < (a.buildFinishedUtc ?? '') ? -1 : 1));
    return withBuild[0]?.buildNumber ?? '';
  }, [consolidated]);

  const runtimeCount = rows.filter((r) => r.category === 'Runtime' || r.category === 'Both').length;
  const configCount = rows.filter((r) => r.category === 'Config' || r.category === 'Both').length;
  const noSuiteCount = rows.filter((r) => r.automatedSuites.length === 0 && r.manualSuites.length === 0).length;
  const changeCount = rows.reduce((n, r) => n + r.changes.length, 0);
  const fileCount = rows.reduce((n, r) => n + r.totalFilesModified, 0);
  const parallelMinutes = scope ? ((scope.runtime.estimatedMinutes + scope.config.estimatedMinutes) / (scope.parallelAgentCount || 1)) : 0;

  const toggleRow = (key: string) =>
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key); else next.add(key);
      return next;
    });

  const connected = connection?.enabled && connection?.credentialConfigured;

  return (
    <div className="flex-1 flex flex-col overflow-hidden bg-bg">
      {/* Connection banner */}
      <div className="flex items-center gap-2 px-4 py-1 border-b border-bdr bg-bg-panel text-[11px] shrink-0">
        <span className={`w-2 h-2 rounded-full ${connected ? 'bg-acc-green' : 'bg-acc-red'}`} />
        <span className="text-text-secondary font-mono">
          {connection
            ? connection.enabled
              ? `ADO ON · ${connection.organization}/${connection.omiProject} · ${connection.mode} · ${connection.credentialSource}${connection.credentialConfigured ? '' : ' · NO CREDENTIAL'}`
              : 'ADO OFF · mock/empty data'
            : '—'}
        </span>
      </div>

      {/* Toolbar */}
      <div className="flex flex-wrap items-center gap-2 px-4 py-2 border-b border-bdr bg-bg-panel shrink-0">
        <span className="text-[10px] uppercase tracking-wider text-text-muted font-medium mr-1">Scope</span>
        {(['Build', 'Weekly', 'Custom', 'Release'] as RegressionScopeKind[]).map((k) => (
          <button
            key={k}
            onClick={() => selectScope(k)}
            className={`px-2.5 py-1 rounded text-xs font-medium border transition-colors ${
              scopeKind === k ? 'bg-accent/20 border-accent text-accent' : 'border-bdr text-text-secondary hover:bg-bg-surface'
            }`}
          >
            {k === 'Build' && latestBuild ? `Build ${latestBuild}` : k}
          </button>
        ))}

        <span className="text-[10px] uppercase tracking-wider text-text-muted font-medium ml-2">From</span>
        <input type="date" value={toDateInput(from)} onChange={(e) => setFrom(new Date(e.target.value))}
          className="bg-bg-surface border border-bdr rounded text-text-primary text-xs font-mono px-2 py-1 outline-none" />
        <span className="text-[10px] uppercase tracking-wider text-text-muted font-medium">To</span>
        <input type="date" value={toDateInput(to)} onChange={(e) => setTo(new Date(e.target.value))}
          className="bg-bg-surface border border-bdr rounded text-text-primary text-xs font-mono px-2 py-1 outline-none" />
        <button onClick={applyRange} className="px-2.5 py-1 rounded text-xs font-medium border border-bdr text-text-secondary hover:bg-bg-surface">Apply</button>

        <span className="text-[10px] uppercase tracking-wider text-text-muted font-medium ml-2">Branch</span>
        <select value={branch ?? ALL_BRANCHES} onChange={(e) => changeBranch(e.target.value)}
          className="bg-bg-surface border border-bdr rounded text-text-primary text-xs px-2 py-1 outline-none max-w-[180px]">
          <option value={ALL_BRANCHES}>{ALL_BRANCHES}</option>
          {branches.map((b) => <option key={b} value={b}>{b}</option>)}
        </select>

        <Toggle label="Runtime" active={showRuntime} onClick={() => setShowRuntime(!showRuntime)} activeClass="bg-acc-green/20 border-acc-green/50 text-acc-green" />
        <Toggle label="Config" active={showConfig} onClick={() => setShowConfig(!showConfig)} activeClass="bg-acc-blue/20 border-acc-blue/50 text-acc-blue" />
        <Toggle label="Human only" active={hideAutomated} onClick={() => setHideAutomated(!hideAutomated)} />
        <Toggle label="Bug" active={filterBug} onClick={() => setFilterBug(!filterBug)} />
        <Toggle label="Story" active={filterStory} onClick={() => setFilterStory(!filterStory)} />
        <Toggle label="IMS" active={filterIms} onClick={() => setFilterIms(!filterIms)} />

        <span className="text-[10px] uppercase tracking-wider text-text-muted font-medium ml-2">Focus</span>
        <select value={selectedDefId ?? 0} onChange={(e) => onComponentPick(e.target.value)}
          className="bg-bg-surface border border-bdr rounded text-text-primary text-xs px-2 py-1 outline-none max-w-[170px]"
          title="Filter the grid to one component / pick a build to inspect">
          <option value={0}>{ALL_COMPONENTS}</option>
          {components.map((c) => <option key={c.definitionId} value={c.definitionId}>{c.name}</option>)}
        </select>
        <select value={selectedBuildId ?? 0} onChange={(e) => onBuildPick(e.target.value)} disabled={!selectedDefId}
          className="bg-bg-surface border border-bdr rounded text-text-primary text-xs px-2 py-1 outline-none max-w-[190px] disabled:opacity-40"
          title="Inspect a specific build of the selected component">
          <option value={0}>(pick build)</option>
          {inspectBuilds.map((b) => <option key={b.buildId} value={b.buildId}>{b.display}</option>)}
        </select>

        <div className="ml-auto flex items-center gap-2">
          <input value={recipients} onChange={(e) => setRecipients(e.target.value)} placeholder="report recipients"
            className="bg-bg-surface border border-bdr rounded text-text-primary text-xs px-2 py-1 outline-none w-40" />
          <button onClick={sendEmail} className="flex items-center gap-1.5 px-2.5 py-1 rounded text-xs font-medium border border-bdr text-text-secondary hover:bg-bg-surface"><Mail size={12} /> Email</button>
          <button onClick={() => runExport('csv')} className="flex items-center gap-1.5 px-2.5 py-1 rounded text-xs font-medium border border-bdr text-text-secondary hover:bg-bg-surface"><Download size={12} /> CSV</button>
          <button onClick={() => runExport('html')} className="flex items-center gap-1.5 px-2.5 py-1 rounded text-xs font-medium border border-bdr text-text-secondary hover:bg-bg-surface"><Download size={12} /> HTML</button>
          <button onClick={() => reload(from, to, branch)} className="flex items-center gap-1.5 px-2.5 py-1 rounded text-xs font-medium border border-bdr text-text-secondary hover:bg-bg-surface"><RotateCw size={12} /> Refresh</button>
        </div>
      </div>

      {/* AI summary banner (collapsible) */}
      {summary && (
        <div className="px-4 py-2 border-b border-bdr bg-bg-panel/60 shrink-0">
          <div className="flex items-center gap-2">
            <Brain size={14} className="text-acc-mauve shrink-0" />
            <span className="text-[10px] uppercase tracking-wider text-acc-mauve font-semibold">AI Summary</span>
            <span className="text-xs text-text-secondary truncate flex-1">{summary.headline}</span>
            <button onClick={() => setSummaryOpen((o) => !o)} className="text-[11px] text-acc-mauve hover:underline shrink-0">
              {summaryOpen ? 'Hide ▴' : 'Details ▾'}
            </button>
          </div>
          {summaryOpen && summary.highlights.length > 0 && (
            <ul className="list-disc pl-8 mt-1.5 text-xs text-text-secondary space-y-0.5">
              {summary.highlights.map((h, i) => <li key={i}>{h}</li>)}
            </ul>
          )}
        </div>
      )}

      {/* Status line */}
      <div className="flex items-center justify-between px-4 py-1 border-b border-bdr text-[10px] font-mono text-text-muted shrink-0">
        <span>
          {syncStatus?.state ?? '—'} · map {syncStatus?.mapVersion ?? '—'} · unresolved:{' '}
          {syncStatus && syncStatus.unresolvedRepositories.length > 0 ? syncStatus.unresolvedRepositories.join(', ') : 'none'}
        </span>
        <span className="text-text-secondary">{statusMessage}</span>
      </div>

      {error && (
        <div className="flex items-center gap-3 bg-acc-red/10 border-b border-acc-red/30 px-4 py-2 text-xs text-acc-red shrink-0">
          <AlertTriangle size={14} className="shrink-0" />
          <span>{error}</span>
        </div>
      )}

      {loading && <div className="text-center text-text-secondary text-sm py-10 font-mono">Loading regression data…</div>}

      {!loading && (
        <div className="flex-1 overflow-auto p-4 flex flex-col gap-3">
          {/* R6 counters */}
          <div className="flex items-center gap-2 flex-wrap">
            <span className="px-2 py-1 rounded bg-acc-green/20 text-acc-green text-xs font-medium">RUNTIME {runtimeCount}</span>
            <span className="px-2 py-1 rounded bg-acc-blue/20 text-acc-blue text-xs font-medium">CONFIG {configCount}</span>
            <span className="px-2 py-1 rounded bg-acc-red/20 text-acc-red text-xs font-medium">NO SUITE {noSuiteCount}</span>
            <span className="text-[11px] font-mono text-text-muted ml-2">
              {changeCount} changes · {rows.length} subsystems · {fileCount} files · {consolidated?.summary.rangeText ?? ''}
            </span>
          </div>

          {focusedRow ? (
            <div className="border border-accent/40 rounded-lg bg-bg-card p-4">
              <div className="flex items-center justify-between mb-3">
                <div className="text-sm font-semibold text-text-primary">
                  Inspecting {focusedRow.component} · build {focusedRow.buildNumber ?? '—'} · {focusedRow.changes.length} change(s)
                </div>
                <button onClick={clearFocus} className="px-2.5 py-1 rounded text-xs font-medium border border-bdr text-text-secondary hover:bg-bg-surface">
                  Back to full grid
                </button>
              </div>
              <RowDetail row={focusedRow} />
            </div>
          ) : (
          <div className="border border-bdr rounded-lg overflow-hidden bg-bg-card">
            <table className="w-full text-xs">
              <thead className="bg-bg-panel text-text-muted uppercase text-[10px] tracking-wider">
                <tr>
                  <th className="w-8" />
                  <th className="text-left px-2 py-2 font-medium w-8">#</th>
                  <th className="text-left px-3 py-2 font-medium">Category</th>
                  <th className="text-left px-3 py-2 font-medium">Repositories</th>
                  <th className="text-left px-3 py-2 font-medium">Subsystems</th>
                  <th className="text-left px-3 py-2 font-medium">Files</th>
                  <th className="text-left px-3 py-2 font-medium">Summary</th>
                  <th className="text-left px-3 py-2 font-medium">Changes</th>
                  <th className="text-left px-3 py-2 font-medium">Risk</th>
                  <th className="text-left px-3 py-2 font-medium">Latest OK</th>
                  <th className="text-left px-3 py-2 font-medium">Auto</th>
                  <th className="text-left px-3 py-2 font-medium">Manual</th>
                  <th className="text-left px-3 py-2 font-medium">Est.</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((row, idx) => {
                  const key = `${row.component}::${row.subsystem}`;
                  const isOpen = expanded.has(key);
                  return (
                    <FragmentRow
                      key={key}
                      row={row}
                      index={idx + 1}
                      isOpen={isOpen}
                      onToggle={() => toggleRow(key)}
                    />
                  );
                })}
                {rows.length === 0 && (
                  <tr><td colSpan={COL_SPAN} className="text-center py-8 text-text-muted font-mono">No subsystems in the current scope/filter.</td></tr>
                )}
              </tbody>
            </table>
          </div>
          )}

          {/* Plan panel */}
          {scope && (
            <div className="grid grid-cols-2 md:grid-cols-6 gap-px bg-bdr border border-bdr rounded-lg overflow-hidden">
              <PlanTile label="RUNTIME · SUBSYSTEMS" value={scope.runtime.subsystems} />
              <PlanTile label="RUNTIME · AUTO/MANUAL/GAPS" value={`${scope.runtime.automatedSuites}/${scope.runtime.manualSuites}/${scope.runtime.gaps}`} danger={scope.runtime.gaps > 0} />
              <PlanTile label="CONFIG · SUBSYSTEMS" value={scope.config.subsystems} />
              <PlanTile label="CONFIG · AUTO/MANUAL/GAPS" value={`${scope.config.automatedSuites}/${scope.config.manualSuites}/${scope.config.gaps}`} danger={scope.config.gaps > 0} />
              <PlanTile label="TOTAL GAPS" value={scope.unmappedSubsystems.length} danger={scope.unmappedSubsystems.length > 0} />
              <PlanTile label="PARALLEL DURATION (EST.)" value={`${Math.round(parallelMinutes)} min`} />
            </div>
          )}
        </div>
      )}
    </div>
  );
}

function FragmentRow({ row, index, isOpen, onToggle }: { row: SubsystemRow; index: number; isOpen: boolean; onToggle: () => void }) {
  return (
    <>
      <tr className={`border-t border-bdr border-l-4 ${rowBorderClass(row.category)} hover:bg-bg-surface/50`}>
        <td className="px-1 text-center">
          <button onClick={onToggle} className="text-text-muted hover:text-text-primary">
            {isOpen ? <ChevronDown size={14} /> : <ChevronRight size={14} />}
          </button>
        </td>
        <td className="px-2 py-2 text-text-muted">{index}</td>
        <td className="px-3 py-2">
          <span className={`px-2 py-0.5 rounded text-[10px] font-medium ${categoryBadgeClass(row.category)}`}>{row.category}</span>
        </td>
        <td className="px-3 py-2 font-mono text-text-primary">
          <span className="font-semibold">{row.component}</span>
          {row.repository && (
            <>
              {' ('}
              {row.repositoryUrl
                ? <a href={row.repositoryUrl} target="_blank" rel="noreferrer" className="text-accent hover:underline">{row.repository}</a>
                : <span className="text-text-secondary">{row.repository}</span>}
              {')'}
            </>
          )}
        </td>
        <td className="px-3 py-2 text-text-secondary">{subsystemsText(row)}</td>
        <td className="px-3 py-2 text-text-secondary">
          {row.filesModified.slice(0, 2).join(', ')}
          {row.filesModified.length > 2 && ` (+${row.filesModified.length - 2} more)`}
        </td>
        <td className="px-3 py-2 text-text-secondary">
          {row.changes[0]?.summary ?? ''}
          {row.changes.length > 1 && ` (+${row.changes.length - 1} more)`}
        </td>
        <td className="px-3 py-2 font-mono text-text-muted">{changeKindLabel(row)}</td>
        <td className="px-3 py-2 text-text-secondary">{row.riskTier}</td>
        <td className="px-3 py-2 font-mono">
          {row.latestSuccessfulBuildUrl
            ? <a href={row.latestSuccessfulBuildUrl} target="_blank" rel="noreferrer" className="text-accent hover:underline">{row.latestSuccessfulBuild ?? '—'}</a>
            : <span className="text-text-muted">{row.latestSuccessfulBuild ?? '—'}</span>}
        </td>
        <td className="px-3 py-2">
          <div className="flex flex-wrap gap-1">
            {row.automatedSuites.map((s) => <span key={s.suiteId} className="px-1.5 py-0.5 rounded bg-acc-green/20 text-acc-green font-mono text-[10px]">{s.suiteId}</span>)}
          </div>
        </td>
        <td className="px-3 py-2">
          <div className="flex flex-wrap gap-1">
            {row.manualSuites.map((s) => (
              <span key={s.suiteId} className={`px-1.5 py-0.5 rounded font-mono text-[10px] ${s.isLinked ? 'bg-acc-blue/20 text-acc-blue' : 'border border-dashed border-acc-blue/50 text-acc-blue'}`}>{s.suiteId}</span>
            ))}
          </div>
        </td>
        <td className="px-3 py-2 font-mono text-text-muted">{estimateText(row)}</td>
      </tr>
      {isOpen && (
        <tr className="bg-bg-panel/40">
          <td colSpan={COL_SPAN} className="px-6 py-3">
            <RowDetail row={row} />
          </td>
        </tr>
      )}
    </>
  );
}

function RowDetail({ row }: { row: SubsystemRow }) {
  const changes = sortedChanges(row);
  const groups = workItemGroups(row);
  const files = modifiedFileLinks(row);
  const tests = functionalTests(row);
  return (
    <div className="flex flex-col gap-3 text-xs">
      {/* Component AI summary */}
      <div className="rounded bg-acc-mauve/10 border border-acc-mauve/25 px-3 py-2">
        <div className="text-[10px] uppercase tracking-wider text-acc-mauve font-semibold mb-1">🧠 Component AI Summary</div>
        <div className="text-text-secondary leading-relaxed">{summarizeComponent(row)}</div>
      </div>

      {row.buildNumber && (
        <div className="font-mono text-text-muted">
          build {row.buildNumber} · repo {row.repository ?? '—'}{row.defaultBranch ? ` · branch ${row.defaultBranch}` : ''}
        </div>
      )}

      {/* Functional tests */}
      <div className="rounded border border-acc-green/30 px-3 py-2">
        <span className="font-semibold text-text-secondary">✅ Functional tests to execute: </span>
        <span className="text-text-primary">{tests.length ? tests.join(', ') : '—'}</span>
      </div>

      {/* Changes */}
      <div>
        <div className="font-semibold text-text-secondary mb-1">Changes (oldest first)</div>
        <div className="flex flex-col gap-0.5 font-mono">
          {changes.map((c) => (
            <div key={c.changeId} className={`${c.kind === 'Automated' ? 'bg-acc-amber/10 rounded px-1' : ''}`}>
              <span className="text-text-muted">{c.observedUtc.slice(0, 16).replace('T', ' ')} </span>
              <span className="font-semibold text-text-muted">{c.kind} </span>
              {c.url
                ? <a href={c.url} target="_blank" rel="noreferrer" className="text-accent hover:underline">{c.summary}</a>
                : <span className="text-text-primary">{c.summary}</span>}
            </div>
          ))}
          {changes.length === 0 && <span className="text-text-muted">—</span>}
        </div>
      </div>

      {/* Work items grouped */}
      {groups.length > 0 && (
        <div>
          <div className="font-semibold text-text-secondary mb-1">Work items</div>
          {groups.map((g) => (
            <div key={g.label} className="mb-1">
              <div className="text-acc-mauve font-semibold">{g.label}</div>
              <div className="pl-3 flex flex-col gap-0.5">
                {g.items.map((w) => (
                  <div key={w.id}>
                    <span className="text-text-muted">{w.createdUtc ? w.createdUtc.slice(0, 10) + ' ' : ''}</span>
                    {w.url
                      ? <a href={w.url} target="_blank" rel="noreferrer" className="text-accent hover:underline">#{w.id} — {w.title}</a>
                      : <span className="text-text-primary">#{w.id} — {w.title}</span>}
                  </div>
                ))}
              </div>
            </div>
          ))}
        </div>
      )}

      {/* Modified files with repo header */}
      {files.length > 0 && (
        <div>
          <div className="font-semibold text-text-secondary mb-1">Modified files</div>
          <div className="font-mono">
            <div className="font-semibold text-text-primary">{row.repository ?? '—'}</div>
            <div className="pl-3 flex flex-col gap-0.5">
              {files.map((f) => (
                <div key={f.path}>
                  <span className="text-text-muted">└─ </span>
                  {f.url
                    ? <a href={f.url} target="_blank" rel="noreferrer" className="text-accent hover:underline">{f.path}</a>
                    : <span className="text-text-primary">{f.path}</span>}
                </div>
              ))}
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

function PlanTile({ label, value, danger }: { label: string; value: string | number; danger?: boolean }) {
  return (
    <div className="bg-bg-panel px-4 py-3">
      <div className="text-[9px] uppercase tracking-wider text-text-muted font-medium mb-1">{label}</div>
      <div className={`font-mono text-base ${danger ? 'text-acc-red' : 'text-text-primary'}`}>{value}</div>
    </div>
  );
}
