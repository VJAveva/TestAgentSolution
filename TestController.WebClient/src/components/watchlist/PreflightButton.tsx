import { useState } from 'react';
import { apiFetch } from '../../lib/api';

/** One finding from the controller's pre-flight check. */
export interface PreflightCheckDto {
  group: string;
  name: string;
  status: 'Pass' | 'Warn' | 'Fail';
  detail: string;
  fixHint?: string | null;
}

export interface PreflightReportDto {
  target: string;
  canRun: boolean;
  hasErrors: boolean;
  hasWarnings: boolean;
  summary: string;
  elapsedSeconds: number;
  checks: PreflightCheckDto[];
  tokens: { token: string; value: string; sourceLayer: string }[];
  text: string;
}

const GROUP_ORDER = [
  'Settings',
  'Controller files',
  'Install sources',
  'Agents',
  'Structure',
  'Disk',
];

const STATUS_RANK: Record<PreflightCheckDto['status'], number> = { Fail: 0, Warn: 1, Pass: 2 };

const STATUS_STYLE: Record<PreflightCheckDto['status'], string> = {
  Fail: 'border-acc-red/40 text-acc-red',
  Warn: 'border-acc-yellow/40 text-acc-yellow',
  Pass: 'border-acc-green/40 text-acc-green',
};

/**
 * "Check only" for a pipeline: runs every pre-flight check on the controller and shows the report
 * without starting anything. Nothing here can trigger a run.
 */
export function PreflightButton({ pipelineTag, className = '' }: { pipelineTag: string; className?: string }) {
  const [report, setReport] = useState<PreflightReportDto | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function run() {
    setBusy(true);
    setError(null);
    try {
      setReport(await apiFetch<PreflightReportDto>(`/api/preflight/${encodeURIComponent(pipelineTag)}`, {
        method: 'POST',
      }));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Pre-flight failed to run');
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <button
        className={`rounded border border-acc-blue/40 px-2 py-0.5 text-[10px] text-acc-blue
          hover:bg-acc-blue/10 disabled:opacity-50 ${className}`}
        title="Run the pre-flight checks without starting the pipeline"
        disabled={busy}
        onClick={(e) => {
          e.stopPropagation();
          void run();
        }}
      >
        {busy ? 'Checking\u2026' : 'Check only'}
      </button>

      {error && <span className="ml-1 text-[10px] text-acc-red">{error}</span>}

      {report && <PreflightReportModal report={report} onClose={() => setReport(null)} />}
    </>
  );
}

export function PreflightReportModal({
  report,
  onClose,
  onContinue,
}: {
  report: PreflightReportDto;
  onClose: () => void;
  onContinue?: () => void;
}) {
  const groups = GROUP_ORDER.map(group => ({
    group,
    checks: report.checks
      .filter(c => c.group === group)
      .sort((a, b) => STATUS_RANK[a.status] - STATUS_RANK[b.status]),
  })).filter(g => g.checks.length > 0);

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 p-4"
      onClick={onClose}
    >
      <div
        className="max-h-[80vh] w-full max-w-3xl overflow-auto rounded-lg border border-bdr bg-bg-card p-4"
        onClick={e => e.stopPropagation()}
      >
        <div className="mb-3 flex items-start justify-between gap-4">
          <div>
            <h2 className="text-sm font-semibold text-text-p">Pre-flight &mdash; {report.target}</h2>
            <p className={`text-xs ${report.hasErrors ? 'text-acc-red' : report.hasWarnings ? 'text-acc-yellow' : 'text-acc-green'}`}>
              {report.summary} (in {report.elapsedSeconds.toFixed(1)}s)
            </p>
          </div>
          <button className="text-xs text-text-muted hover:text-text-p" onClick={onClose}>
            Close
          </button>
        </div>

        {groups.map(({ group, checks }) => (
          <section key={group} className="mb-3">
            <h3 className="mb-1 text-[11px] font-semibold uppercase tracking-wide text-text-muted">{group}</h3>
            <ul className="space-y-1">
              {checks.map((check, i) => (
                <li key={`${check.name}-${i}`} className={`rounded border px-2 py-1 text-xs ${STATUS_STYLE[check.status]}`}>
                  <span className="font-semibold">{check.status}</span>
                  <span className="text-text-p"> &mdash; {check.name}: </span>
                  <span className="text-text-s">{check.detail}</span>
                  {check.fixHint && <div className="mt-0.5 text-[11px] text-text-muted">Fix: {check.fixHint}</div>}
                </li>
              ))}
            </ul>
          </section>
        ))}

        {onContinue && (
          <div className="mt-4 flex justify-end gap-2">
            <button className="rounded border border-bdr px-3 py-1 text-xs text-text-s" onClick={onClose}>
              Cancel
            </button>
            {/* Only warnings may be waved through; errors never offer this. */}
            {!report.hasErrors && (
              <button
                className="rounded bg-acc-yellow/80 px-3 py-1 text-xs font-semibold text-black hover:bg-acc-yellow"
                onClick={onContinue}
              >
                Continue anyway
              </button>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
