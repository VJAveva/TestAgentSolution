import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
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

const CHIP_CAP = 3;

// Caps a cell's list so no row can grow past two chip lines; the overflow opens in a popover rather than
// expanding the row, and the full list is still rendered inside it.
function CappedChips({ label, items, cap = CHIP_CAP }: { label: string; items: ReactNode[]; cap?: number }) {
  const [open, setOpen] = useState(false);
  const wrapRef = useRef<HTMLDivElement>(null);
  const btnRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') { setOpen(false); btnRef.current?.focus(); }
    };
    const onDown = (e: MouseEvent) => {
      if (!wrapRef.current?.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('keydown', onKey);
    document.addEventListener('mousedown', onDown);
    return () => { document.removeEventListener('keydown', onKey); document.removeEventListener('mousedown', onDown); };
  }, [open]);

  if (items.length === 0) return <span className="text-text-muted">—</span>;
  const hidden = items.length - cap;

  return (
    <div ref={wrapRef} className="relative">
      <div className="flex flex-wrap gap-1">
        {items.slice(0, cap)}
        {hidden > 0 && (
          <button ref={btnRef} onClick={() => setOpen((o) => !o)} aria-expanded={open}
            aria-label={`Show all ${items.length} ${label}`}
            className="px-1.5 py-0.5 rounded border border-dashed border-bdr text-[10px] text-text-muted hover:text-text-primary hover:border-accent/60">
            +{hidden} more
          </button>
        )}
      </div>
      {open && (
        <div role="dialog" aria-label={`All ${items.length} ${label}`}
          className="absolute right-0 z-30 mt-1 w-[420px] max-h-64 overflow-auto rounded-md border border-bdr bg-bg-card shadow-lg p-2">
          <div className="text-[10px] uppercase tracking-wider text-text-muted mb-1.5">{label} · {items.length}</div>
          <div className="flex flex-col gap-1 items-start [&_*]:max-w-none [&_*]:whitespace-normal [&_*]:overflow-visible">{items}</div>
        </div>
      )}
    </div>
  );
}

const SELECT_ROW_H = 28;

// Replaces the native <datalist>, which silently ignored any typed value that didn't match an option.
function SearchSelect({ label, value, placeholder, options, onPick, width = 'w-[190px]' }: {
  label: string; value: string; placeholder: string; options: string[]; onPick: (value: string) => void; width?: string;
}) {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [cursor, setCursor] = useState(0);
  const wrapRef = useRef<HTMLDivElement>(null);
  const btnRef = useRef<HTMLButtonElement>(null);

  const matches = useMemo(
    () => options.filter((o) => o.toLowerCase().includes(query.trim().toLowerCase())),
    [options, query],
  );

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') { setOpen(false); btnRef.current?.focus(); } };
    const onDown = (e: MouseEvent) => { if (!wrapRef.current?.contains(e.target as Node)) setOpen(false); };
    document.addEventListener('keydown', onKey);
    document.addEventListener('mousedown', onDown);
    return () => { document.removeEventListener('keydown', onKey); document.removeEventListener('mousedown', onDown); };
  }, [open]);

  const commit = (v: string) => { onPick(v); setOpen(false); setQuery(''); btnRef.current?.focus(); };

  return (
    <div ref={wrapRef} className="relative inline-flex shrink-0">
      <button ref={btnRef} onClick={() => { setOpen((o) => !o); setQuery(''); setCursor(0); }}
        aria-expanded={open} aria-haspopup="listbox" title={`${label}: ${value || placeholder}`}
        className={`inline-flex items-center gap-1.5 h-7 px-2 rounded border text-xs ${width} ${
          open ? 'border-accent text-accent bg-accent/10' : 'bg-bg-surface border-bdr text-text-primary hover:border-accent/50'}`}>
        <span className="text-text-muted shrink-0">{label}</span>
        <span className="truncate flex-1 text-left font-mono">{value || placeholder}</span>
        <ChevronDown size={11} className="shrink-0 opacity-60" />
      </button>
      {open && (
        <div className="absolute left-0 top-8 z-30 w-[290px] rounded-md border border-bdr bg-bg-card shadow-lg p-1.5">
          <input autoFocus value={query} placeholder={`Filter ${label.toLowerCase()}\u2026`}
            onChange={(e) => { setQuery(e.target.value); setCursor(0); }}
            onKeyDown={(e) => {
              if (e.key === 'ArrowDown') { e.preventDefault(); setCursor((c) => Math.min(c + 1, matches.length - 1)); }
              else if (e.key === 'ArrowUp') { e.preventDefault(); setCursor((c) => Math.max(c - 1, 0)); }
              else if (e.key === 'Enter' && matches[cursor] != null) { e.preventDefault(); commit(matches[cursor]); }
            }}
            className="w-full h-7 px-2 mb-1 rounded border border-bdr bg-bg-surface text-text-primary text-xs outline-none focus:border-accent" />
          <div role="listbox" aria-label={label} className="overflow-y-auto" style={{ maxHeight: SELECT_ROW_H * 4 }}>
            {matches.map((o, i) => (
              <button key={o} role="option" aria-selected={o === value}
                onMouseEnter={() => setCursor(i)} onClick={() => commit(o)}
                className={`w-full flex items-center text-left px-2 h-7 rounded text-xs font-mono truncate ${
                  i === cursor ? 'bg-accent/15 text-accent' : o === value ? 'text-accent' : 'text-text-secondary hover:bg-bg-surface'}`}>
                <span className="truncate">{o}</span>
              </button>
            ))}
            {matches.length === 0 && <div className="px-2 py-2 text-[11px] text-text-muted">No match for \u201c{query}\u201d.</div>}
          </div>
        </div>
      )}
    </div>
  );
}

function InlineStat({ label, value, flag, ok, active, onClick }: { label: string; value: ReactNode; flag?: boolean; ok?: boolean; active?: boolean; onClick?: () => void }) {
  const tone = flag ? 'text-acc-red' : ok ? 'text-acc-green' : 'text-text-primary';
  const body = (
    <>
      <span className={`font-mono text-[13px] font-semibold leading-none ${tone}`}>{value}</span>
      <span className={`text-[11px] leading-none ${flag ? 'text-acc-red/80' : 'text-text-muted'}`}>{label}</span>
    </>
  );
  if (!onClick) return <span className="inline-flex items-baseline gap-1.5 px-2.5 whitespace-nowrap">{body}</span>;
  return (
    <button onClick={onClick} aria-label={`Filter to ${value} ${label}`} aria-pressed={!!active}
      className={`inline-flex items-baseline gap-1.5 px-2.5 py-1 rounded whitespace-nowrap hover:bg-bg-surface ${active ? 'bg-bg-surface' : ''}`}>
      {body}
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
  const indexHealth = useRegressionStore((s) => s.indexHealth);
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
    // These load once on mount and swallow their errors, so a single failed load would otherwise leave the
    // pickers empty until a full page reload.
    if (branches.length === 0) fetchBranches();
    if (components.length === 0) fetchComponents();
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
      {/* CC-H2 — identity, inline stats, actions */}
      <div className="flex items-center gap-2.5 px-4 h-11 border-b border-bdr bg-bg-panel shrink-0">
        <span className="text-sm font-semibold text-text-primary shrink-0">Code churn</span>
        <Divider />
        <span className="font-mono text-[11px] text-text-muted truncate shrink-0 max-w-[200px]"
          title={`${connection?.omiProject ?? 'ADO'} / ${selectedComponentName ?? 'All components'}`}>
          {connection?.omiProject ?? 'ADO'}<span className="px-1 text-bdr">/</span>{selectedComponentName ?? 'All components'}
        </span>
        <div className="flex items-center flex-1 min-w-0 overflow-hidden divide-x divide-bdr/60">
          <InlineStat label="Changes" value={changeCount} />
          <InlineStat label="Files" value={fileCount} />
          <InlineStat label="Subsystems" value={rows.length} />
          <InlineStat label="No suite" value={noSuiteCount} flag={noSuiteCount > 0} active={unmappedOnly} onClick={() => setUnmappedOnly(!unmappedOnly)} />
          <InlineStat label="Impact map" value={syncStatus?.mapVersion ?? '—'} />
          <InlineStat label="Unresolved" value={syncStatus?.unresolvedRepositories.length ?? 0} ok={(syncStatus?.unresolvedRepositories.length ?? 0) === 0} />
        </div>
        {statusMessage && <span title={statusMessage} className="text-[11px] text-text-secondary font-mono truncate max-w-[180px] shrink-0">{statusMessage}</span>}
        <button title={connectionTitle}
          className={`inline-flex items-center gap-1.5 px-2.5 h-7 rounded-full border text-[11px] shrink-0 ${connected ? 'border-acc-green/40 text-acc-green bg-acc-green/10' : 'border-acc-amber/40 text-acc-amber bg-acc-amber/10'}`}>
          <span className={`w-1.5 h-1.5 rounded-full ${connected ? 'bg-acc-green' : 'bg-acc-amber'}`} />
          {connection?.enabled ? 'ADO' : 'Mock data'}
        </button>
        {lastLoadedAt && <span className="text-[11px] text-text-muted font-mono shrink-0 hidden xl:inline">updated {lastLoadedAt.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</span>}
        <button onClick={() => reload(from, to, branch)} title="Refresh" className="inline-flex items-center justify-center h-7 w-7 rounded border border-bdr text-text-secondary hover:bg-bg-surface hover:text-text-primary shrink-0"><RotateCw size={13} /></button>
        <Divider />
        <button onClick={() => summarizeWithAi(from, to, branch, filterState)} disabled={aiLoading} title="AI summary"
          className="inline-flex items-center gap-1.5 h-7 px-2.5 rounded border border-acc-mauve/50 text-acc-mauve text-xs hover:bg-acc-mauve/10 disabled:opacity-40 shrink-0">
          <Sparkles size={12} /> {aiLoading ? 'Summarizing\u2026' : 'AI'}
        </button>
        <div className="relative shrink-0">
          <button onClick={() => setExportMenuOpen((o) => !o)}
            className="inline-flex items-center gap-1.5 h-7 px-3 rounded bg-accent text-white text-xs font-semibold hover:bg-accent/90">
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

      {/* CC-H4 — single filter rail */}
      <div className="flex flex-wrap items-center gap-2 px-4 py-1.5 border-b border-bdr bg-bg-panel shrink-0">
        <div className="inline-flex rounded-md border border-bdr overflow-hidden h-7 shrink-0">
          {(['Build', 'Weekly', 'Custom', 'Release'] as RegressionScopeKind[]).map((k) => (
            <button key={k} onClick={() => selectScope(k)}
              className={`px-2.5 text-xs border-r border-bdr/60 last:border-r-0 transition-colors ${scopeKind === k ? 'bg-accent/20 text-accent' : 'bg-bg-surface text-text-secondary hover:text-text-primary'}`}>
              {k === 'Build' && latestBuild ? `Build ${latestBuild}` : k}
            </button>
          ))}
        </div>
        <SearchSelect label="Branch" value={!branch || branch === ALL_BRANCHES ? '' : branch} placeholder="all" width="w-[190px]"
          options={[ALL_BRANCHES, ...branches]} onPick={(v) => { setBranchText(v); changeBranch(v); }} />
        <SearchSelect label="Component" value={selectedComponentName ?? ''} placeholder="all" width="w-[190px]"
          options={[ALL_COMPONENTS, ...components.map((c) => c.name)]}
          onPick={(v) => {
            setComponentText(v === ALL_COMPONENTS ? '' : v);
            if (v === ALL_COMPONENTS) { onComponentPick('0'); return; }
            const comp = components.find((c) => c.name === v);
            if (comp) onComponentPick(String(comp.definitionId));
          }} />
        {scopeKind === 'Custom' && (
          <Unit label="From">
            <input type="date" value={toDateInput(from)} onChange={(e) => setFrom(new Date(e.target.value))}
              className="bg-bg-surface border border-bdr rounded text-text-primary text-xs font-mono px-2 h-7 outline-none" />
            <span className="text-[11px] text-text-muted">To</span>
            <input type="date" value={toDateInput(to)} onChange={(e) => setTo(new Date(e.target.value))}
              className="bg-bg-surface border border-bdr rounded text-text-primary text-xs font-mono px-2 h-7 outline-none" />
            <button onClick={applyRange} className="h-7 px-3 rounded border border-accent/60 text-accent text-xs hover:bg-accent/10">Apply</button>
          </Unit>
        )}
        {selectedDefId != null && (
          <select value={selectedBuildId ?? 0} onChange={(e) => onBuildPick(e.target.value)}
            className="bg-bg-surface border border-bdr rounded text-text-primary text-xs px-2 h-7 outline-none max-w-[190px] shrink-0"
            title="Inspect a specific build of the selected component">
            <option value={0}>(pick build)</option>
            {inspectBuilds.map((b) => <option key={b.buildId} value={b.buildId}>{b.display}</option>)}
          </select>
        )}
        <Divider />
        <Chip label="Runtime" count={runtimeCount} active={showRuntime} onClick={() => setShowRuntime(!showRuntime)} tone="green" />
        <Chip label="Config" count={configCount} active={showConfig} onClick={() => setShowConfig(!showConfig)} tone="blue" />
        <Chip label="Human only" active={hideAutomated} onClick={() => setHideAutomated(!hideAutomated)} title="Hide automated / version-bump changes" />
        <Divider />
        <Chip label="Bug" active={filterBug} onClick={() => setFilterBug(!filterBug)} />
        <Chip label="Story" active={filterStory} onClick={() => setFilterStory(!filterStory)} />
        <Chip label="IMS" active={filterIms} onClick={() => setFilterIms(!filterIms)} />
        <Divider />
        <Chip label="No suite mapped" count={noSuiteCount} active={unmappedOnly} onClick={() => setUnmappedOnly(!unmappedOnly)} tone="alert" />
        <Chip label="All changes" active={showAllChanges} onClick={() => setShowAllChanges(!showAllChanges)} title="Include universal-package / YAML noise changes" />
        <div className="flex-1" />
        {anyFilterActive && (
          <button onClick={clearFilters} className="text-[11px] text-text-muted underline underline-offset-2 hover:text-text-primary shrink-0">Clear filters</button>
        )}
      </div>


      {/* AI summary banner (collapsible) */}
      {indexHealth && indexHealth.status !== 'Ready' && (
        <div className={`flex items-start gap-2 px-4 py-2 border-b shrink-0 ${indexHealth.status === 'Stale' ? 'bg-acc-amber/10 border-acc-amber/30 text-acc-amber' : 'bg-acc-red/10 border-acc-red/30 text-acc-red'}`}>
          <AlertTriangle size={14} className="shrink-0 mt-0.5" />
          <div className="min-w-0">
            <div className="font-semibold">Impacted test cases are unavailable: {indexHealth.status}</div>
            <div className="text-[11px] opacity-90 break-words">{indexHealth.message}</div>
          </div>
        </div>
      )}

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
        <div className="flex-1 min-h-0 overflow-hidden p-4 flex flex-col gap-3">

          {focusedRow ? (
            <div className="border border-accent/40 rounded-lg bg-bg-card p-4 flex-1 min-h-0 overflow-auto">
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
          <div className="border border-bdr rounded-lg overflow-hidden bg-bg-card flex-1 min-h-0 flex flex-col">
            <div className="flex-1 min-h-0 overflow-auto">
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
            <div className="grid grid-cols-2 md:grid-cols-6 gap-px bg-bdr border border-bdr rounded-lg overflow-hidden shrink-0">
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
        <td className="px-3 py-2 text-text-secondary">
          <CappedChips label="subsystems" cap={2} items={(row.solutionNames ?? []).map((s) => (
            <span key={s} title={s} className="px-1.5 py-0.5 rounded bg-bg-surface text-text-secondary text-[10px] max-w-[140px] truncate">{s}</span>
          ))} />
        </td>
        <td className="px-3 py-2 text-text-secondary">
          <CappedChips label="files" cap={2} items={row.filesModified.map((f) => (
            <span key={f} title={f} className="px-1.5 py-0.5 rounded bg-bg-surface text-text-secondary font-mono text-[10px] max-w-[150px] truncate">{f}</span>
          ))} />
        </td>
        <td className="px-3 py-2 text-text-secondary">
          <div className="line-clamp-2" title={row.changes[0]?.summary ?? ''}>
            {row.changes[0]?.summary ?? ''}
            {row.changes.length > 1 && ` (+${row.changes.length - 1} more)`}
          </div>
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
          <CappedChips label="manual test cases" cap={2} items={row.manualSuites.map((s) => (
            s.url
              ? <a key={s.suiteId} href={s.url} target="_blank" rel="noreferrer" title={s.title} className={`px-1.5 py-0.5 rounded font-mono text-[10px] bg-acc-blue/20 text-acc-blue hover:underline max-w-[150px] truncate`}>TC {s.suiteId}{s.title ? ` - ${s.title}` : ''}</a>
              : <span key={s.suiteId} title={s.title} className="px-1.5 py-0.5 rounded font-mono text-[10px] border border-dashed border-acc-blue/50 text-acc-blue max-w-[150px] truncate">TC {s.suiteId}{s.title ? ` - ${s.title}` : ''}</span>
          ))} />
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
        {analysis?.indexHealthMessage && (
          <div className="rounded border border-acc-amber/30 bg-acc-amber/10 px-2.5 py-2 text-acc-amber">
            Indexed matches unavailable. The offline change analysis below is still valid. {analysis.indexHealthMessage}
          </div>
        )}
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
                {m.linkedWorkItems && m.linkedWorkItems.length > 0 && (
                  <div className="text-[11px] mt-0.5 flex flex-wrap items-center gap-1">
                    <span className="text-text-muted">Verifies:</span>
                    {m.linkedWorkItems.map((w) => (
                      w.url
                        ? <a key={w.id} href={w.url} target="_blank" rel="noreferrer" className="text-accent hover:underline">{w.kind} {w.id} - {w.title}</a>
                        : <span key={w.id} className="text-text-primary">{w.kind} {w.id} - {w.title}</span>
                    ))}
                  </div>
                )}
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
