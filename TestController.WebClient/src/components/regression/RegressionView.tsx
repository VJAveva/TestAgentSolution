import { useEffect, useMemo, useState, type ReactNode } from 'react';
import { AlertTriangle, RotateCw, ChevronRight, ChevronDown, Download, Mail, Brain, Sparkles } from 'lucide-react';
import { useRegression } from '../../hooks/useRegression';
import { useRegressionStore } from '../../stores/regressionStore';
import type { RegressionBuildRef, RegressionCategoryKind, RegressionScopeKind, SubsystemRow, ImpactedComponentAnalysis } from '../../types/api';
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
import type { FilterState } from './helpers';

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

function Unit({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="inline-flex items-center gap-2 whitespace-nowrap shrink-0">
      <span className="text-[11px] text-text-muted shrink-0">{label}</span>
      {children}
    </div>
  );
}

function Divider() {
  return <div className="w-px self-stretch bg-bdr/70 mx-3 shrink-0" aria-hidden />;
}

type ChipTone = 'default' | 'green' | 'blue' | 'alert';
function Chip({ label, count, active, onClick, tone = 'default', title }: { label: string; count?: number; active: boolean; onClick: () => void; tone?: ChipTone; title?: string }) {
  const on = {
    default: 'bg-accent/15 border-accent text-accent',
    green: 'bg-acc-green/15 border-acc-green/60 text-acc-green',
    blue: 'bg-acc-blue/15 border-acc-blue/60 text-acc-blue',
    alert: 'bg-acc-red/15 border-acc-red text-acc-red',
  }[tone];
  return (
    <button onClick={onClick} title={title}
      className={`inline-flex items-center gap-1.5 h-7 px-2.5 rounded-full border text-xs whitespace-nowrap transition-colors ${
        active ? on : 'bg-bg-surface border-bdr text-text-secondary hover:text-text-primary'
      }`}>
      {label}
      {count != null && <span className="font-mono text-[11px] opacity-60">{count}</span>}
    </button>
  );
}

function Stat({ label, value, flag, ok, active, onClick }: { label: string; value: ReactNode; flag?: boolean; ok?: boolean; active?: boolean; onClick?: () => void }) {
  return (
    <button onClick={onClick} disabled={!onClick}
      className={`flex-1 text-left px-4 py-2.5 border-r border-bdr/50 last:border-r-0 transition-colors ${onClick ? 'hover:bg-bg-surface cursor-pointer' : 'cursor-default'} ${active ? 'bg-bg-surface' : ''}`}>
      <span className={`block font-mono text-lg leading-tight ${flag ? 'text-acc-red' : ok ? 'text-acc-green' : 'text-text-primary'}`}>{value}</span>
      <span className={`text-[11px] ${flag ? 'text-acc-red/80' : 'text-text-muted'}`}>{label}</span>
    </button>
  );
}

export default function RegressionView() {
  const { loadAll, fetchBranches, fetchComponents, fetchBuilds, fetchBuildImpact, downloadReport, emailReport, summarizeWithAi } = useRegression();
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
  const showAllChanges = useRegressionStore((s) => s.showAllChanges);
  const filterBug = useRegressionStore((s) => s.filterBug);
  const filterStory = useRegressionStore((s) => s.filterStory);
  const filterIms = useRegressionStore((s) => s.filterIms);
  const setShowRuntime = useRegressionStore((s) => s.setShowRuntime);
  const setShowConfig = useRegressionStore((s) => s.setShowConfig);
  const setHideAutomated = useRegressionStore((s) => s.setHideAutomated);
  const setShowAllChanges = useRegressionStore((s) => s.setShowAllChanges);
  const setFilterBug = useRegressionStore((s) => s.setFilterBug);
  const setFilterStory = useRegressionStore((s) => s.setFilterStory);
  const setFilterIms = useRegressionStore((s) => s.setFilterIms);
  const loading = useRegressionStore((s) => s.loading);
  const aiLoading = useRegressionStore((s) => s.aiLoading);
  const error = useRegressionStore((s) => s.error);

  const [scopeKind, setScopeKind] = useState<RegressionScopeKind>('Build');
  const [from, setFrom] = useState(() => {
    const d = new Date();
    d.setDate(d.getDate() - 2);
    return d;
  });
  const [to, setTo] = useState(() => new Date());
  const [branch, setBranch] = useState<string | null>(null);
  const [selectedDefId, setSelectedDefId] = useState<number | null>(null);
  const [selectedComponentName, setSelectedComponentName] = useState<string | null>(null);
  // Empty = "all branches" (shown via placeholder). A non-empty sentinel here would filter the native datalist and hide every real branch.
  const [branchText, setBranchText] = useState<string>('');
  const [componentText, setComponentText] = useState<string>('');
  const [inspectBuilds, setInspectBuilds] = useState<RegressionBuildRef[]>([]);
  const [selectedBuildId, setSelectedBuildId] = useState<number | null>(null);
  const [focusedRow, setFocusedRow] = useState<SubsystemRow | null>(null);
  const [recipients, setRecipients] = useState('');
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  const [summaryOpen, setSummaryOpen] = useState(false);
  const [statusMessage, setStatusMessage] = useState('');
  const [unmappedOnly, setUnmappedOnly] = useState(false);
  const [exportMenuOpen, setExportMenuOpen] = useState(false);
  const [emailOpen, setEmailOpen] = useState(false);
  const [lastLoadedAt, setLastLoadedAt] = useState<Date | null>(null);

  useEffect(() => {
    loadAll(from, to, null);
    fetchBranches();
    fetchComponents();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Keep the searchable inputs in sync when the selection changes elsewhere (scope buttons, reset).
  useEffect(() => { setBranchText(branch ?? ''); }, [branch]);
  useEffect(() => { setComponentText(selectedComponentName ?? ''); }, [selectedComponentName]);
  useEffect(() => { if (consolidated) setLastLoadedAt(new Date()); }, [consolidated]);

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
    if (kind === 'Build') { newFrom = new Date(today); newFrom.setDate(today.getDate() - 2); }
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

  const filterState: FilterState = {
    showRuntime, showConfig, hideAutomated, showAllChanges, filterBug, filterStory, filterIms, component: selectedComponentName,
  };

  const runExport = async (format: 'csv' | 'html' | 'xlsx') => {
    try {
      setStatusMessage(`Generating ${format.toUpperCase()} report\u2026`);
      await downloadReport(from, to, branch, format, filterState);
      setStatusMessage(`${format.toUpperCase()} report downloaded.`);
    } catch (e) {
      setStatusMessage(`Export failed: ${e instanceof Error ? e.message : e}`);
    }
  };

  const sendEmail = async () => {
    if (!recipients.trim()) { setStatusMessage('Enter at least one recipient email.'); return; }
    try {
      setStatusMessage(`Emailing report to ${recipients}…`);
      await emailReport(from, to, branch, recipients, filterState);
      setStatusMessage(`Report emailed to ${recipients}.`);
    } catch (e) {
      setStatusMessage(`Email failed: ${e instanceof Error ? e.message : e}`);
    }
  };

  const rows = useMemo(() => {
    const base = applyFilters(consolidated?.rows ?? [], {
      showRuntime, showConfig, hideAutomated, showAllChanges, filterBug, filterStory, filterIms, component: selectedComponentName,
    });
    return unmappedOnly ? base.filter((r) => r.automatedSuites.length === 0 && r.manualSuites.length === 0) : base;
  }, [consolidated, showRuntime, showConfig, hideAutomated, showAllChanges, filterBug, filterStory, filterIms, selectedComponentName, unmappedOnly]);

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

  const anyFilterActive = filterBug || filterStory || filterIms || unmappedOnly || showAllChanges || !showRuntime || !showConfig || !hideAutomated;
  const clearFilters = () => {
    setShowRuntime(true); setShowConfig(true); setHideAutomated(true); setShowAllChanges(false);
    setFilterBug(false); setFilterStory(false); setFilterIms(false); setUnmappedOnly(false);
  };
  const connectionTitle = connection
    ? connection.enabled
      ? `ADO ON · ${connection.organization}/${connection.omiProject} · ${connection.mode} · ${connection.credentialSource}${connection.credentialConfigured ? '' : ' · NO CREDENTIAL'}`
      : 'ADO OFF · mock/empty data'
    : '—';

  return (
    <div className="flex-1 flex flex-col overflow-hidden bg-bg">
      {/* W1/W2 — identity, mode badge, refresh */}
      <div className="flex items-center gap-3 px-4 py-2 border-b border-bdr bg-bg-panel shrink-0">
        <div className="min-w-0">
          <h2 className="text-sm font-semibold text-text-primary leading-tight">Code churn</h2>
          <div className="font-mono text-[11px] text-text-muted truncate">
            {connection?.omiProject ?? 'ADO'}<span className="px-1 text-bdr">/</span>{selectedComponentName ?? 'All components'}
          </div>
        </div>
        <div className="flex-1" />
        {statusMessage && <span className="text-[11px] text-text-secondary font-mono truncate max-w-[300px]">{statusMessage}</span>}
        <button title={connectionTitle}
          className={`inline-flex items-center gap-1.5 px-2.5 h-7 rounded-full border text-[11px] ${connected ? 'border-acc-green/40 text-acc-green bg-acc-green/10' : 'border-acc-amber/40 text-acc-amber bg-acc-amber/10'}`}>
          <span className={`w-1.5 h-1.5 rounded-full ${connected ? 'bg-acc-green' : 'bg-acc-amber'}`} />
          {connection?.enabled ? 'ADO' : 'Mock data'}
        </button>
        {lastLoadedAt && <span className="text-[11px] text-text-muted font-mono">updated {lastLoadedAt.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</span>}
        <button onClick={() => reload(from, to, branch)} title="Refresh" className="inline-flex items-center justify-center h-7 w-7 rounded border border-bdr text-text-secondary hover:bg-bg-surface hover:text-text-primary"><RotateCw size={13} /></button>
      </div>

      {/* W3/W4/W5 — scope row: branch, range, (custom dates), component, build */}
      <div className="flex flex-wrap items-center gap-y-2 px-4 py-2 border-b border-bdr bg-bg-panel shrink-0">
        <Unit label="Branch">
          <input list="branch-options" value={branchText}
            onChange={(e) => {
              const v = e.target.value;
              setBranchText(v);
              if (v === '' || v === ALL_BRANCHES) changeBranch(ALL_BRANCHES);
              else if (branches.includes(v)) changeBranch(v);
            }}
            placeholder="all branches"
            className="bg-bg-surface border border-bdr rounded text-text-primary text-xs font-mono px-2 h-7 outline-none w-[220px]" />
          <datalist id="branch-options">
            <option value={ALL_BRANCHES} />
            {branches.map((b) => <option key={b} value={b} />)}
          </datalist>
        </Unit>
        <Divider />
        <Unit label="Range">
          <div className="inline-flex rounded-md border border-bdr overflow-hidden h-7">
            {(['Build', 'Weekly', 'Custom', 'Release'] as RegressionScopeKind[]).map((k) => (
              <button key={k} onClick={() => selectScope(k)}
                className={`px-3 text-xs border-r border-bdr/60 last:border-r-0 transition-colors ${scopeKind === k ? 'bg-accent/20 text-accent' : 'bg-bg-surface text-text-secondary hover:text-text-primary'}`}>
                {k === 'Build' && latestBuild ? `Build ${latestBuild}` : k}
              </button>
            ))}
          </div>
        </Unit>
        {scopeKind === 'Custom' && (
          <>
            <Divider />
            <Unit label="From">
              <input type="date" value={toDateInput(from)} onChange={(e) => setFrom(new Date(e.target.value))}
                className="bg-bg-surface border border-bdr rounded text-text-primary text-xs font-mono px-2 h-7 outline-none" />
              <span className="text-[11px] text-text-muted">To</span>
              <input type="date" value={toDateInput(to)} onChange={(e) => setTo(new Date(e.target.value))}
                className="bg-bg-surface border border-bdr rounded text-text-primary text-xs font-mono px-2 h-7 outline-none" />
              <button onClick={applyRange} className="h-7 px-3 rounded border border-accent/60 text-accent text-xs hover:bg-accent/10">Apply</button>
            </Unit>
          </>
        )}
        <div className="flex-1" />
        <Unit label="Component">
          <input list="component-options" value={componentText}
            onChange={(e) => {
              const v = e.target.value;
              setComponentText(v);
              if (v === '' || v === ALL_COMPONENTS) { onComponentPick('0'); return; }
              const comp = components.find((c) => c.name === v);
              if (comp) onComponentPick(String(comp.definitionId));
            }}
            placeholder={ALL_COMPONENTS}
            title="Filter the grid to one component (type to search)"
            className="bg-bg-surface border border-bdr rounded text-text-primary text-xs px-2 h-7 outline-none w-[190px]" />
          <datalist id="component-options">
            {components.map((c) => <option key={c.definitionId} value={c.name} />)}
          </datalist>
        </Unit>
        {selectedDefId != null && (
          <>
            <Divider />
            <Unit label="Build">
              <select value={selectedBuildId ?? 0} onChange={(e) => onBuildPick(e.target.value)}
                className="bg-bg-surface border border-bdr rounded text-text-primary text-xs px-2 h-7 outline-none max-w-[200px]"
                title="Inspect a specific build of the selected component">
                <option value={0}>(pick build)</option>
                {inspectBuilds.map((b) => <option key={b.buildId} value={b.buildId}>{b.display}</option>)}
              </select>
            </Unit>
          </>
        )}
      </div>

      {/* W6..W10 — filter row: context, author, work item, coverage, noise, actions */}
      <div className="flex flex-wrap items-center gap-y-2 px-4 py-2 border-b border-bdr bg-bg-panel shrink-0">
        <Unit label="Context">
          <div className="inline-flex gap-1.5">
            <Chip label="Runtime" count={runtimeCount} active={showRuntime} onClick={() => setShowRuntime(!showRuntime)} tone="green" />
            <Chip label="Config" count={configCount} active={showConfig} onClick={() => setShowConfig(!showConfig)} tone="blue" />
          </div>
        </Unit>
        <Divider />
        <Unit label="Author">
          <Chip label="Human only" active={hideAutomated} onClick={() => setHideAutomated(!hideAutomated)} title="Hide automated / version-bump changes" />
        </Unit>
        <Divider />
        <Unit label="Work item">
          <div className="inline-flex gap-1.5">
            <Chip label="Bug" active={filterBug} onClick={() => setFilterBug(!filterBug)} />
            <Chip label="Story" active={filterStory} onClick={() => setFilterStory(!filterStory)} />
            <Chip label="IMS" active={filterIms} onClick={() => setFilterIms(!filterIms)} />
          </div>
        </Unit>
        <Divider />
        <Unit label="Coverage">
          <Chip label="No suite mapped" count={noSuiteCount} active={unmappedOnly} onClick={() => setUnmappedOnly(!unmappedOnly)} tone="alert" />
        </Unit>
        <Divider />
        <Unit label="Noise">
          <Chip label="All changes" active={showAllChanges} onClick={() => setShowAllChanges(!showAllChanges)} title="Include universal-package / YAML noise changes" />
        </Unit>
        <button onClick={clearFilters} disabled={!anyFilterActive}
          className="ml-3 text-[11px] text-text-muted underline underline-offset-2 hover:text-text-primary disabled:opacity-40 disabled:no-underline">Clear</button>
        <div className="flex-1" />
        <div className="inline-flex items-center gap-2">
          <button onClick={() => summarizeWithAi(from, to, branch, filterState)} disabled={aiLoading}
            className="inline-flex items-center gap-1.5 h-8 px-3 rounded border border-acc-mauve/50 text-acc-mauve text-xs hover:bg-acc-mauve/10 disabled:opacity-40">
            <Sparkles size={12} /> {aiLoading ? 'Summarizing\u2026' : 'AI summary'}
          </button>
          <div className="relative">
            <button onClick={() => setExportMenuOpen((o) => !o)}
              className="inline-flex items-center gap-1.5 h-8 px-3.5 rounded bg-accent text-white text-xs font-semibold hover:bg-accent/90">
              <Download size={12} /> Export <ChevronDown size={12} />
            </button>
            {exportMenuOpen && (
              <div className="absolute right-0 mt-1 w-44 rounded-md border border-bdr bg-bg-card shadow-lg z-20 py-1" onMouseLeave={() => setExportMenuOpen(false)}>
                <button onClick={() => { runExport('xlsx'); setExportMenuOpen(false); }} className="w-full text-left px-3 py-1.5 text-xs text-text-secondary hover:bg-bg-surface hover:text-text-primary">Excel (.xlsx)</button>
                <button onClick={() => { runExport('csv'); setExportMenuOpen(false); }} className="w-full text-left px-3 py-1.5 text-xs text-text-secondary hover:bg-bg-surface hover:text-text-primary">CSV</button>
                <button onClick={() => { runExport('html'); setExportMenuOpen(false); }} className="w-full text-left px-3 py-1.5 text-xs text-text-secondary hover:bg-bg-surface hover:text-text-primary">HTML</button>
                <div className="my-1 border-t border-bdr" />
                <button onClick={() => { setEmailOpen(true); setExportMenuOpen(false); }} className="w-full text-left px-3 py-1.5 text-xs text-text-secondary hover:bg-bg-surface hover:text-text-primary flex items-center gap-1.5"><Mail size={12} /> Email report…</button>
              </div>
            )}
          </div>
        </div>
      </div>

      {/* W11 — stat strip (replaces the dot-joined status line) */}
      <div className="flex bg-bg-panel border-b border-bdr shrink-0">
        <Stat label="Changes" value={changeCount} />
        <Stat label="Files" value={fileCount} />
        <Stat label="Subsystems" value={rows.length} />
        <Stat label="No suite mapped" value={noSuiteCount} flag active={unmappedOnly} onClick={() => setUnmappedOnly(!unmappedOnly)} />
        <Stat label="Impact map" value={syncStatus?.mapVersion ?? '—'} />
        <Stat label="Unresolved paths" value={syncStatus?.unresolvedRepositories.length ?? 0} ok={(syncStatus?.unresolvedRepositories.length ?? 0) === 0} />
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

      {/* Email report dialog */}
      {emailOpen && (
        <div className="fixed inset-0 z-30 flex items-center justify-center bg-black/50" onClick={() => setEmailOpen(false)}>
          <div className="w-[440px] rounded-lg border border-bdr bg-bg-card shadow-xl" onClick={(e) => e.stopPropagation()}>
            <div className="px-4 py-3 border-b border-bdr text-sm font-semibold text-text-primary">Email churn report</div>
            <div className="p-4 flex flex-col gap-3">
              <label className="flex flex-col gap-1.5">
                <span className="text-[11px] text-text-muted">Recipients (comma or semicolon separated)</span>
                <textarea value={recipients} onChange={(e) => setRecipients(e.target.value)} rows={2} placeholder="name@aveva.com; sp-qa-leads@aveva.com"
                  className="bg-bg-surface border border-bdr rounded text-text-primary text-xs px-2 py-1.5 outline-none resize-y" />
              </label>
              <p className="text-[11px] text-text-muted">Sends the HTML report with a CSV attachment for the current scope and filters.</p>
            </div>
            <div className="px-4 py-3 border-t border-bdr flex justify-end gap-2">
              <button onClick={() => setEmailOpen(false)} className="h-8 px-3 rounded border border-bdr text-text-secondary text-xs hover:bg-bg-surface">Cancel</button>
              <button onClick={async () => { const had = recipients.trim(); await sendEmail(); if (had) setEmailOpen(false); }} className="h-8 px-4 rounded bg-accent text-white text-xs font-semibold hover:bg-accent/90">Send report</button>
            </div>
          </div>
        </div>
      )}

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
            <div className="overflow-auto max-h-[70vh]">
            <table className="w-full min-w-[1200px] text-xs">
              <thead className="bg-bg-panel text-text-muted uppercase text-[10px] tracking-wider sticky top-0 z-10">
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
  const { fetchTestMatches } = useRegression();
  const changes = sortedChanges(row);
  const groups = workItemGroups(row);
  const files = modifiedFileLinks(row);
  const tests = functionalTests(row);
  const [analysis, setAnalysis] = useState<ImpactedComponentAnalysis | null>(null);
  const [matchesLoading, setMatchesLoading] = useState(false);
  const [matchesError, setMatchesError] = useState<string | null>(null);

  // Lazily analyze this component's changes when the row expands (engine matches + offline fallback).
  useEffect(() => {
    let cancelled = false;
    setMatchesLoading(true);
    setMatchesError(null);
    fetchTestMatches(row)
      .then((a) => { if (!cancelled) setAnalysis(a); })
      .catch((e) => { if (!cancelled) setMatchesError(e instanceof Error ? e.message : 'Failed to analyze changes'); })
      .finally(() => { if (!cancelled) setMatchesLoading(false); });
    return () => { cancelled = true; };
  }, [row, fetchTestMatches]);

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

      {/* Impacted test cases (impact-mapping engine) with an offline change-analysis fallback */}
      <div>
        <div className="font-semibold text-text-secondary mb-1">
          🎯 Impacted test cases for {row.component}{analysis && analysis.matches.length > 0 ? ` (${analysis.matches.length})` : ''}
        </div>
        {matchesLoading && <div className="text-text-muted font-mono">Analyzing changes…</div>}
        {matchesError && <div className="text-acc-red">{matchesError}</div>}
        {analysis && analysis.matches.length > 0 && (
          <div className="flex flex-col gap-1.5">
            {analysis.matches.map((m) => (
              <div key={m.testCaseId} className="rounded border border-bdr bg-bg-panel/40 px-2.5 py-1.5">
                <div className="flex items-center gap-2 flex-wrap">
                  {m.testCaseUrl
                    ? <a href={m.testCaseUrl} target="_blank" rel="noreferrer" className="text-accent hover:underline font-mono">#{m.testCaseId}</a>
                    : <span className="text-text-muted font-mono">#{m.testCaseId}</span>}
                  <span className="text-text-primary font-medium">{m.testCaseTitle}</span>
                  <span className="ml-auto flex items-center gap-1.5 shrink-0">
                    <span className="px-1.5 py-0.5 rounded bg-bg-surface text-text-muted text-[10px] uppercase">{m.matchType}</span>
                    <span className="px-1.5 py-0.5 rounded bg-acc-green/15 text-acc-green text-[10px] font-mono">{m.confidencePercent}%</span>
                  </span>
                </div>
                <div className="text-[11px] text-text-muted mt-0.5">
                  Parent feature: {m.parentFeatureId > 0 ? `#${m.parentFeatureId}` : 'does not exist'}
                </div>
                {m.matchReason && <div className="text-text-secondary mt-0.5 leading-relaxed">{m.matchReason}</div>}
                {m.description && <div className="text-text-muted mt-0.5 leading-relaxed italic line-clamp-3">{m.description}</div>}
              </div>
            ))}
          </div>
        )}
        {!matchesLoading && !matchesError && analysis && analysis.matches.length === 0 && (
          <div className="flex flex-col gap-2">
            <div className="text-text-muted italic">No matched test cases in the index — recommendation from the code changes:</div>
            {analysis.changeSummary && (
              <div className="rounded border border-bdr bg-bg-panel/40 px-2.5 py-2 text-text-secondary leading-relaxed max-h-32 overflow-y-auto whitespace-pre-wrap break-words">
                {analysis.changeSummary}
              </div>
            )}
            {analysis.recommendedTests.length > 0 && (
              <div>
                <div className="text-text-secondary font-semibold mb-1">Recommended tests</div>
                <div className="flex flex-wrap gap-1">
                  {analysis.recommendedTests.map((t) => (
                    <span
                      key={t.name}
                      title={t.relevant ? 'Relevant to these changes' : undefined}
                      className={`px-1.5 py-0.5 rounded text-[10px] font-mono ${t.relevant ? 'bg-acc-green/20 text-acc-green' : 'bg-bg-surface text-text-muted'}`}
                    >
                      {t.relevant ? '★ ' : ''}{t.name}
                    </span>
                  ))}
                </div>
              </div>
            )}
          </div>
        )}
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
