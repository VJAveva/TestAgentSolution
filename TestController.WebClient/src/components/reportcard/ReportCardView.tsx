import { useEffect, useState } from 'react';
import { RotateCw, AlertTriangle } from 'lucide-react';
import { useReportCard } from '../../hooks/useReportCard';
import { useReportCardStore } from '../../stores/reportCardStore';
import type {
  BuildReportCard,
  ReportSeverity,
  CiResult,
  AgentResult,
  PsrResult,
  FailureEntry,
  TrendPoint,
} from '../../types/api';

// ── Severity → fixed report-card palette ─────────────────────────────────────
function sevText(sev: ReportSeverity): string {
  switch (sev) {
    case 'Pass': return 'text-rc-success';
    case 'Info': return 'text-rc-info';
    case 'Warn': return 'text-rc-warning';
    case 'Fail': return 'text-rc-danger';
  }
}
function sevDot(sev: ReportSeverity): string {
  switch (sev) {
    case 'Pass': return 'bg-rc-success';
    case 'Info': return 'bg-rc-info';
    case 'Warn': return 'bg-rc-warning';
    case 'Fail': return 'bg-rc-danger';
  }
}
function sevCellBorder(sev: ReportSeverity): string {
  switch (sev) {
    case 'Pass': return 'border-rc-success bg-rc-bg-success';
    case 'Warn': return 'border-rc-warning bg-rc-bg-warning';
    case 'Fail': return 'border-rc-danger bg-rc-bg-danger';
    default:     return 'border-rc-border bg-rc-bg-secondary';
  }
}
function sevBarColor(sev: ReportSeverity): string {
  switch (sev) {
    case 'Pass': return 'bg-rc-success';
    case 'Info': return 'bg-rc-info';
    case 'Warn': return 'bg-rc-warning';
    case 'Fail': return 'bg-rc-danger';
  }
}
function gradeCircleColors(sev: ReportSeverity): string {
  switch (sev) {
    case 'Pass': return 'border-rc-success bg-rc-bg-success text-rc-success';
    case 'Info': return 'border-rc-info bg-rc-bg-info text-rc-info';
    case 'Warn': return 'border-rc-warning bg-rc-bg-warning text-rc-warning';
    case 'Fail': return 'border-rc-danger bg-rc-bg-danger text-rc-danger';
  }
}

const pct = (n: number) => `${n.toFixed(1)}%`;

// ── Top-level view ───────────────────────────────────────────────────────────
export default function ReportCardView() {
  const { fetchBuilds, fetchCard } = useReportCard();
  const card = useReportCardStore(s => s.card);
  const builds = useReportCardStore(s => s.builds);
  const selectedBuild = useReportCardStore(s => s.selectedBuild);
  const loading = useReportCardStore(s => s.loading);
  const error = useReportCardStore(s => s.error);
  const [initialized, setInitialized] = useState(false);

  useEffect(() => {
    (async () => {
      await fetchBuilds();
      await fetchCard();
      setInitialized(true);
    })();
  }, [fetchBuilds, fetchCard]);

  const onSelect = (build: string) => {
    fetchCard(build);
  };

  return (
    <div className="flex-1 overflow-auto bg-rc-bg-primary">
      <div className="mx-auto max-w-[1400px] flex flex-col gap-3.5 p-5">
        {/* Build selector */}
        <div className="flex items-center justify-between gap-3 flex-wrap">
          <div className="flex items-center gap-2">
            <span className="text-[10px] uppercase tracking-wider text-rc-text-tertiary font-medium">
              Build
            </span>
            <select
              value={selectedBuild ?? ''}
              onChange={e => onSelect(e.target.value)}
              className="bg-rc-bg-tertiary border border-rc-border rounded text-rc-text-primary text-xs font-mono px-3 py-1.5 outline-none focus:border-rc-info"
            >
              {builds.length === 0 && <option value="">— no builds —</option>}
              {builds.map(b => (
                <option key={b} value={b}>{b}</option>
              ))}
            </select>
          </div>
          <button
            onClick={() => fetchCard(selectedBuild ?? undefined)}
            className="flex items-center gap-1.5 px-3 py-1.5 rounded text-xs font-medium border border-rc-border bg-rc-bg-tertiary text-rc-text-primary hover:bg-rc-bg-secondary transition-colors"
          >
            <RotateCw size={12} /> Refresh
          </button>
        </div>

        {loading && (
          <div className="text-center text-rc-text-secondary text-sm py-16 font-mono">
            Loading report card…
          </div>
        )}

        {error && (
          <div className="flex items-center gap-3 bg-rc-bg-danger border border-rc-danger/40 rounded px-4 py-3 text-xs text-rc-danger">
            <AlertTriangle size={14} className="shrink-0" />
            <span>{error}</span>
          </div>
        )}

        {!loading && !error && initialized && (!card || !card.hasData) && (
          <div className="text-center text-rc-text-tertiary text-sm py-16 font-mono">
            No report data for this build.
          </div>
        )}

        {!loading && card && card.hasData && (
          <>
            <HeroCard card={card} />
            <CiGrid cis={card.cis} />
            <AgentTable agents={card.agents} />
            <div className="grid grid-cols-1 lg:grid-cols-[3fr_2fr] gap-3.5">
              <PsrCard psrs={card.psrs} />
              <TrendCard trend={card.trend} delta={card.deltaVsLast} />
            </div>
            <FailuresCard failures={card.failures} totalFailed={card.failedTests} />
            <FooterCard card={card} />
          </>
        )}
      </div>
    </div>
  );
}

// ── Card shell ───────────────────────────────────────────────────────────────
function Card({ children }: { children: React.ReactNode }) {
  return (
    <div className="bg-rc-bg-primary border border-rc-border rounded-lg overflow-hidden">
      {children}
    </div>
  );
}

function SectionHeader({ left, right }: { left: string; right?: string }) {
  return (
    <div className="flex justify-between items-center px-4 py-2.5 bg-rc-bg-secondary border-b border-rc-border text-[10px] font-medium uppercase tracking-wider text-rc-text-tertiary">
      <span>{left}</span>
      {right && <span className="text-[9px]">{right}</span>}
    </div>
  );
}

// ── Hero + Grade + KPI strip ─────────────────────────────────────────────────
function HeroCard({ card }: { card: BuildReportCard }) {
  const triggered = card.startedUtc
    ? new Date(card.startedUtc).toLocaleString()
    : new Date(card.generatedUtc).toLocaleString();
  const delta = card.deltaVsLast;

  return (
    <Card>
      <div
        className="px-6 py-5 border-b border-rc-border"
        style={{ background: 'var(--rc-hero)' }}
      >
        <div className="flex justify-between items-start gap-5 flex-wrap">
          <div>
            <div className="text-[11px] font-medium text-rc-text-tertiary uppercase tracking-wider mb-1.5">
              Build Report Card
            </div>
            <div className="font-mono text-2xl font-medium text-rc-text-primary mb-1">
              {card.buildNumber}
            </div>
            <div className="text-[11px] text-rc-text-secondary font-mono">
              Triggered: {triggered} by {card.triggeredBy} · Duration: {card.durationLabel}
            </div>
          </div>
          <div className="flex items-center gap-3.5">
            <div
              className={`w-[90px] h-[90px] rounded-full flex flex-col items-center justify-center border-2 shrink-0 ${gradeCircleColors(card.grade.severity)}`}
            >
              <div className="text-[38px] font-semibold leading-none">{card.grade.letter}</div>
              <div className="text-[10px] font-mono mt-0.5">{pct(card.grade.score)}</div>
            </div>
            <div className="flex flex-col gap-1">
              <div className={`text-[13px] font-medium ${sevText(card.grade.severity)}`}>
                {card.grade.verdict}
              </div>
              <div className="text-[10px] text-rc-text-tertiary font-mono">
                {card.passedTests.toLocaleString()} pass / {card.failedTests.toLocaleString()} fail / {card.skippedTests.toLocaleString()} skip
              </div>
              <div className="text-[10px] text-rc-text-tertiary font-mono">
                {card.regressionCount} regressions · {card.flakyCount} flaky · {card.psrErrorCount} PSR error
              </div>
              {delta != null && (
                <div className="text-[10px] text-rc-text-tertiary font-mono mt-1">
                  vs last build:{' '}
                  <span className={delta < 0 ? 'text-rc-danger' : 'text-rc-success'}>
                    {delta < 0 ? '↓' : '↑'} {Math.abs(delta).toFixed(1)}%
                  </span>
                </div>
              )}
            </div>
          </div>
        </div>
      </div>

      {/* KPI strip */}
      <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-6 border-t border-rc-border">
        <Kpi label="Total tests" value={card.totalTests.toLocaleString()} sub={`across ${card.cis.length} CIs`} />
        <Kpi label="Pass rate" value={pct(card.passRate)} sub="target: 98%" valueClass={sevText(card.grade.severity)} />
        <Kpi label="Failures" value={String(card.failedTests)} sub={`in ${card.cis.filter(c => c.failed > 0).length} of ${card.cis.length} CIs`} valueClass={card.failedTests > 0 ? 'text-rc-danger' : undefined} />
        <Kpi label="Regressions" value={String(card.regressionCount)} sub="passed previously" valueClass={card.regressionCount > 0 ? 'text-rc-danger' : undefined} />
        <Kpi label="Agents" value={`${card.agents.length} / ${card.agents.length}`} sub="all completed" />
        <Kpi label="PSR runs" value={`${card.psrPassCount} / ${card.psrTotalCount}`} sub={card.psrPassCount < card.psrTotalCount ? `${card.psrTotalCount - card.psrPassCount} failed` : 'all passed'} valueClass={card.psrPassCount < card.psrTotalCount ? 'text-rc-warning' : undefined} />
      </div>
    </Card>
  );
}

function Kpi({ label, value, sub, valueClass }: { label: string; value: string; sub: string; valueClass?: string }) {
  return (
    <div className="px-4 py-3 border-r last:border-r-0 border-rc-border bg-rc-bg-secondary">
      <div className="text-[9px] text-rc-text-tertiary uppercase tracking-wide font-medium mb-1">{label}</div>
      <div className={`font-mono text-[17px] font-medium ${valueClass ?? 'text-rc-text-primary'}`}>{value}</div>
      <div className="text-[9px] text-rc-text-tertiary font-mono mt-0.5">{sub}</div>
    </div>
  );
}

// ── CI grid ──────────────────────────────────────────────────────────────────
function CiGrid({ cis }: { cis: CiResult[] }) {
  return (
    <Card>
      <SectionHeader left="CI Results · pass rate per Configuration Item" right={`${cis.length} CIs`} />
      <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-5 gap-2 p-3">
        {cis.map(ci => (
          <div key={ci.name} className={`p-2.5 rounded-md border ${sevCellBorder(ci.severity)}`}>
            <div className="flex justify-between items-center mb-1.5">
              <div className="font-mono text-[11px] font-medium text-rc-text-primary">{ci.name}</div>
              <span className={`w-2 h-2 rounded-full ${sevDot(ci.severity)}`} />
            </div>
            <div className="text-[10px] font-mono text-rc-text-secondary">
              {ci.passed} / {ci.total} · {pct(ci.passRate)}
            </div>
            <div className="mt-1.5 h-[3px] rounded-sm bg-rc-bg-tertiary overflow-hidden">
              <div className={`h-full ${sevBarColor(ci.severity)}`} style={{ width: `${Math.min(ci.passRate, 100)}%` }} />
            </div>
          </div>
        ))}
      </div>
    </Card>
  );
}

// ── Agent table ──────────────────────────────────────────────────────────────
// Header and rows share ONE grid-template (uceCols) so columns line up
// pixel-for-pixel at every width. The header is frozen outside the scroll
// region; only the body scrolls, inside a bounded-height container with its
// own scrollbar. The scrollbar gutter is reserved so the header never shifts.
const uceCols =
  'minmax(180px,2.6fr) minmax(120px,1.8fr) minmax(120px,1.4fr) 56px 56px 56px 80px 64px';

function AgentTable({ agents }: { agents: AgentResult[] }) {
  return (
    <Card>
      <SectionHeader left={`Agent Execution · ${agents.length} agents · per-agent results`} right="use case + agent" />

      {/* Frozen header — same grid template as rows, right padding reserves the gutter */}
      <div
        className="grid bg-rc-bg-secondary border-b border-rc-border pr-[var(--rc-sbw)] text-[9px] font-medium uppercase tracking-wide text-rc-text-tertiary"
        style={{ gridTemplateColumns: uceCols, ['--rc-sbw' as string]: '14px' }}
      >
        <Uh>Use Case</Uh>
        <Uh>Agent</Uh>
        <Uh>Pass / Fail / Skip distribution</Uh>
        <Uh right>Pass</Uh>
        <Uh right>Fail</Uh>
        <Uh right>Skip</Uh>
        <Uh right>Time</Uh>
        <Uh right>%</Uh>
      </div>

      {/* Body — the only thing that scrolls; bounded height + stable gutter */}
      <div
        className="max-h-[440px] overflow-y-scroll overflow-x-hidden"
        style={{ scrollbarGutter: 'stable' }}
      >
        {agents.map((a, i) => {
          const tint = a.severity === 'Fail' ? 'bg-rc-bg-danger' : a.severity === 'Warn' ? 'bg-rc-bg-warning' : '';
          const total = a.total || 1;
          const passW = (a.passed / total) * 100;
          const failW = (a.failed / total) * 100;
          const skipW = (a.skipped / total) * 100;
          return (
            <div
              key={`${a.useCase}-${a.agentName}-${i}`}
              className={`grid border-b border-rc-border last:border-b-0 ${tint}`}
              style={{ gridTemplateColumns: uceCols }}
            >
              <Uc className="text-rc-text-primary font-medium" title={a.useCase}>{a.useCase}</Uc>
              <Uc className="text-rc-text-primary font-medium" title={a.agentName}>
                <span className={`inline-block w-1.5 h-1.5 rounded-full mr-1.5 shrink-0 ${sevDot(a.severity)}`} />
                <span className="truncate">{a.agentName}</span>
              </Uc>
              <Uc>
                <div className="w-full h-1.5 bg-rc-bg-tertiary rounded-sm overflow-hidden flex">
                  <div className="h-full bg-rc-success" style={{ width: `${passW}%` }} />
                  <div className="h-full bg-rc-danger" style={{ width: `${failW}%` }} />
                  <div className="h-full bg-rc-text-tertiary" style={{ width: `${skipW}%` }} />
                </div>
              </Uc>
              <Uc right>{a.passed}</Uc>
              <Uc right className={a.failed > 0 ? 'text-rc-danger' : ''}>{a.failed}</Uc>
              <Uc right>{a.skipped}</Uc>
              <Uc right>{a.durationLabel}</Uc>
              <Uc right className={sevText(a.severity)}>{pct(a.passRate)}</Uc>
            </div>
          );
        })}
      </div>
    </Card>
  );
}

// Header cell within the use-case-execution grid
function Uh({ children, right }: { children: React.ReactNode; right?: boolean }) {
  return (
    <div className={`px-3 py-2 truncate ${right ? 'text-right' : 'text-left'}`}>
      {children}
    </div>
  );
}

// Data cell within the use-case-execution grid
function Uc({
  children,
  right,
  className,
  title,
}: {
  children: React.ReactNode;
  right?: boolean;
  className?: string;
  title?: string;
}) {
  return (
    <div
      title={title}
      className={`px-3 py-2.5 font-mono text-[11px] text-rc-text-secondary flex items-center min-w-0 truncate ${
        right ? 'justify-end text-right' : 'text-left'
      } ${className ?? ''}`}
    >
      {children}
    </div>
  );
}

function Th({ children, right }: { children: React.ReactNode; right?: boolean }) {
  return (
    <th className={`px-3 py-2 bg-rc-bg-secondary border-b border-rc-border text-[9px] font-medium uppercase tracking-wide text-rc-text-tertiary ${right ? 'text-right' : 'text-left'}`}>
      {children}
    </th>
  );
}
function Td({ children, right, className }: { children: React.ReactNode; right?: boolean; className?: string }) {
  return (
    <td className={`px-3 py-2.5 border-b border-rc-border font-mono text-[11px] text-rc-text-secondary ${right ? 'text-right' : 'text-left'} ${className ?? ''}`}>
      {children}
    </td>
  );
}

// ── PSR cards ────────────────────────────────────────────────────────────────
function PsrCard({ psrs }: { psrs: PsrResult[] }) {
  const psrSev = (o: PsrResult['outcome']): ReportSeverity =>
    o === 'Passed' ? 'Pass' : o === 'PassedWithWarnings' ? 'Warn' : o === 'Failed' ? 'Fail' : 'Info';
  const psrLabel = (o: PsrResult['outcome']): string =>
    o === 'Passed' ? 'PASSED' : o === 'PassedWithWarnings' ? 'PASS w/ WARN' : o === 'Failed' ? 'FAILED' : 'PENDING';

  return (
    <Card>
      <SectionHeader left={`PSR Validation · ${psrs.length} customer scenarios`} right="production-like runs" />
      <div className="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-3 gap-2.5 p-3">
        {psrs.map(p => {
          const sev = psrSev(p.outcome);
          return (
            <div key={p.name} className={`p-3 rounded-md border ${sevCellBorder(sev)}`}>
              <div className="text-[11px] font-medium text-rc-text-primary mb-1">{p.name}</div>
              <div className={`text-[9px] font-mono mb-2 ${sevText(sev)}`}>
                {psrLabel(p.outcome)} · {p.durationLabel}
              </div>
              <PsrStat label="Tags" value={p.tags.toLocaleString()} />
              {p.throughput && <PsrStat label="Throughput" value={p.throughput} />}
              {p.warnings > 0 && <PsrStat label="Warnings" value={String(p.warnings)} bad />}
              {p.errors > 0
                ? <PsrStat label="Errors" value={p.errorDetail || String(p.errors)} bad />
                : (p.warnings === 0 && <PsrStat label="Errors" value="0" />)}
            </div>
          );
        })}
      </div>
    </Card>
  );
}

function PsrStat({ label, value, bad }: { label: string; value: string; bad?: boolean }) {
  return (
    <div className={`flex justify-between text-[9px] font-mono py-0.5 ${bad ? 'text-rc-danger' : 'text-rc-text-secondary'}`}>
      <span>{label}</span>
      <span>{value}</span>
    </div>
  );
}

// ── Trend ────────────────────────────────────────────────────────────────────
function TrendCard({ trend, delta }: { trend: TrendPoint[]; delta?: number }) {
  return (
    <Card>
      <SectionHeader left={`Pass rate · last ${trend.length} builds`} right="trend" />
      <div className="p-4 flex flex-col gap-2">
        <div className="flex items-end gap-1.5 h-20">
          {trend.map((t, i) => {
            const passH = Math.max(Math.min(t.passRate, 100), 0);
            const failH = 100 - passH;
            return (
              <div key={`${t.label}-${i}`} className="flex-1 flex flex-col items-center gap-1 h-full">
                <div
                  className="w-full flex-1 flex flex-col justify-end rounded-sm overflow-hidden"
                  style={t.isCurrent ? { outline: '1.5px solid rgb(var(--rc-warning))', outlineOffset: '1px' } : undefined}
                >
                  <div className="bg-rc-success" style={{ height: `${passH}%` }} />
                  <div className="bg-rc-danger" style={{ height: `${failH}%` }} />
                </div>
                <div className={`text-[9px] font-mono ${t.isCurrent ? 'text-rc-warning font-semibold' : 'text-rc-text-tertiary'}`}>
                  {t.isCurrent ? '★' : t.label}
                </div>
              </div>
            );
          })}
        </div>
        {delta != null && (
          <div className="text-[10px] text-rc-text-tertiary font-mono text-center mt-1">
            ★ this build · {delta < 0 ? '↓' : '↑'} {Math.abs(delta).toFixed(1)}% vs {trend.length}-build avg
          </div>
        )}
      </div>
    </Card>
  );
}

// ── Top failures ─────────────────────────────────────────────────────────────
function FailuresCard({ failures, totalFailed }: { failures: FailureEntry[]; totalFailed: number }) {
  const tagClass = (kind: FailureEntry['pattern']): string => {
    switch (kind) {
      case 'Regression': return 'bg-rc-bg-danger text-rc-danger border border-rc-danger';
      case 'New':        return 'bg-rc-bg-danger text-rc-danger';
      case 'Flaky':      return 'bg-rc-bg-warning text-rc-warning';
      case 'Chronic':    return 'bg-rc-bg-tertiary text-rc-text-tertiary';
      case 'Cascading':  return 'bg-rc-bg-warning text-rc-warning';
      case 'Resolved':   return 'bg-rc-bg-success text-rc-success';
      default:           return 'bg-rc-bg-tertiary text-rc-text-tertiary';
    }
  };

  return (
    <Card>
      <SectionHeader left="Top Failures · prioritized for triage" right={`${totalFailed} total · showing top ${failures.length}`} />
      <div className="overflow-x-auto">
        <table className="w-full border-collapse text-[11px]">
          <thead>
            <tr>
              <Th>Test</Th>
              <Th>CI</Th>
              <Th>Failed On</Th>
              <Th>Pattern</Th>
              <Th>Owner</Th>
              <Th>First Seen</Th>
            </tr>
          </thead>
          <tbody>
            {failures.map((f, i) => (
              <tr key={`${f.testName}-${i}`} className="hover:bg-rc-bg-secondary">
                <Td className="text-rc-text-primary font-medium">{f.testName}</Td>
                <Td>{f.ci}</Td>
                <Td>{f.failedOnAgents.join(', ')}</Td>
                <Td>
                  <span className={`inline-block px-2 py-0.5 rounded-full text-[9px] font-medium font-mono ${tagClass(f.pattern)}`}>
                    {f.patternLabel}
                  </span>
                </Td>
                <Td>{f.owner}</Td>
                <Td>{f.firstSeen}</Td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {totalFailed > failures.length && (
        <div className="px-3 py-2.5 text-[10px] text-rc-info text-center border-t border-rc-border font-mono">
          {totalFailed} failures total
        </div>
      )}
    </Card>
  );
}

// ── Footer ───────────────────────────────────────────────────────────────────
function FooterCard({ card }: { card: BuildReportCard }) {
  return (
    <Card>
      <div className="flex justify-between items-center px-4 py-3 bg-rc-bg-secondary border-t border-rc-border text-[10px] text-rc-text-tertiary flex-wrap gap-2.5">
        <div>Report generated {new Date(card.generatedUtc).toLocaleString()}</div>
        <div className="font-mono">Grade {card.grade.letter} · {pct(card.grade.score)}</div>
      </div>
    </Card>
  );
}
