import { useEffect, useState } from 'react';
import { X, AlertTriangle, FileText } from 'lucide-react';
import { apiFetch } from '../../lib/api';
import type { FailureAnalysisReport, FailureSignature, TestExecutionRecord } from '../../types/api';

type AnalysisReport = FailureAnalysisReport;
type HistoryEntry = TestExecutionRecord;

const PATTERN_COLORS: Record<string, { bg: string; fg: string; label: string }> = {
  SystemicRegression: { bg: 'bg-red-500/15', fg: 'text-red-400', label: 'SYSTEMIC REGRESSION' },
  CascadingFailures: { bg: 'bg-amber-500/15', fg: 'text-amber-400', label: 'CASCADING FAILURES' },
  FlakyTest: { bg: 'bg-yellow-500/15', fg: 'text-yellow-400', label: 'FLAKY TEST' },
  ChronicFailure: { bg: 'bg-purple-500/15', fg: 'text-purple-400', label: 'CHRONIC FAILURE' },
  Resolved: { bg: 'bg-green-500/15', fg: 'text-green-400', label: 'RESOLVED' },
  NewFailure: { bg: 'bg-orange-500/15', fg: 'text-orange-400', label: 'NEW FAILURE' },
  None: { bg: 'bg-slate-500/15', fg: 'text-slate-400', label: 'NO PATTERN' },
};

interface Props {
  testName: string;
  onClose: () => void;
  onViewLog?: (build: string, stepIndex?: number) => void;
}

export default function FailureAnalysisDialog({ testName, onClose, onViewLog }: Props) {
  const [report, setReport] = useState<AnalysisReport | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    apiFetch<AnalysisReport>(`/api/results/analyze/${encodeURIComponent(testName)}?builds=10`)
      .then(setReport)
      .catch(err => setError(err?.message ?? 'Failed to load analysis'));
  }, [testName]);

  const colors = report ? PATTERN_COLORS[report.pattern] ?? PATTERN_COLORS.None : PATTERN_COLORS.None;
  const latestBuild = report?.history?.[0]?.buildName;
  const latestFailedStep = report?.signatures?.[0]?.failedStepIndex;

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm">
      <div className="bg-bg-panel rounded-lg border border-bdr w-[900px] max-h-[85vh] flex flex-col">
        <div className="flex items-center justify-between px-5 py-3 border-b border-bdr">
          <div>
            <h2 className="text-lg font-bold text-text-primary">Failure Pattern Analysis</h2>
            <p className="text-xs text-text-muted mt-1 font-mono">{testName}</p>
          </div>
          <button onClick={onClose} className="p-1.5 hover:bg-white/5 rounded">
            <X size={18} className="text-text-muted" />
          </button>
        </div>

        <div className="flex-1 overflow-auto p-5 space-y-4">
          {error && (
            <div className="bg-red-500/10 border border-red-500/30 rounded p-3 text-red-300 text-sm">
              {error}
            </div>
          )}

          {!report && !error && (
            <div className="text-center text-text-muted py-12">Analyzing failure pattern�</div>
          )}

          {report && (
            <>
              {/* Verdict */}
              <div className={`rounded-lg p-4 border ${colors.bg} border-current ${colors.fg}`}>
                <div className="flex items-start justify-between gap-4">
                  <div className="flex-1">
                    <div className="flex items-center gap-2 mb-2">
                      <AlertTriangle size={16} />
                      <span className="font-bold text-sm">{colors.label}</span>
                    </div>
                    <p className="text-sm text-text-primary">{report.verdict}</p>
                  </div>
                  <div className="text-right shrink-0 bg-black/20 rounded px-3 py-2">
                    <div className="text-[10px] text-text-muted uppercase">Confidence</div>
                    <div className="text-2xl font-bold">{report.confidence}%</div>
                  </div>
                </div>
              </div>

              {/* Build timeline */}
              <div className="bg-white/5 rounded p-3">
                <div className="text-[10px] font-bold uppercase text-text-muted mb-2">
                  Build history (newest ? oldest, {report.totalBuildsAnalyzed} builds)
                </div>
                <div className="flex flex-wrap gap-1.5">
                  {report.history.map((h, i) => (
                    <div
                      key={i}
                      title={`${h.buildName} � ${h.outcome} (${new Date(h.buildDate).toLocaleString()})`}
                      className={`w-7 h-7 rounded flex items-center justify-center text-white text-xs font-bold ${
                        h.outcome === 'Passed' ? 'bg-green-600' :
                        h.outcome === 'Failed' ? 'bg-red-600' : 'bg-slate-600'
                      }`}
                    >
                      {h.outcome === 'Passed' ? '?' : h.outcome === 'Failed' ? '?' : '�'}
                    </div>
                  ))}
                </div>
                <div className="flex gap-4 mt-3 text-xs text-text-muted">
                  <span>Consecutive: <span className="text-text-primary font-bold">{report.consecutiveFailures}</span></span>
                  <span>Total failures: <span className="text-text-primary font-bold">{report.totalFailures}</span></span>
                  <span>Flake rate: <span className="text-text-primary font-bold">{report.flakeRate.toFixed(0)}%</span></span>
                </div>
              </div>

              {/* Signatures */}
              {report.signatures.length > 0 && (
                <div className="bg-white/5 rounded p-3">
                  <div className="text-[10px] font-bold uppercase text-text-muted mb-2">
                    Failure signatures {report.allSignaturesMatch && (
                      <span className="ml-2 text-green-400">(all match � same root cause)</span>
                    )}
                  </div>
                  <div className="overflow-x-auto">
                    <table className="w-full text-xs">
                      <thead>
                        <tr className="text-text-muted border-b border-bdr">
                          <th className="text-left py-1.5 px-2 font-medium">Build</th>
                          <th className="text-left py-1.5 px-2 font-medium">Step</th>
                          <th className="text-left py-1.5 px-2 font-medium">Error type</th>
                          <th className="text-left py-1.5 px-2 font-medium">Normalized message</th>
                          <th className="text-left py-1.5 px-2 font-medium">Agent</th>
                        </tr>
                      </thead>
                      <tbody>
                        {report.signatures.map((s, i) => (
                          <tr key={i} className="border-b border-bdr/50">
                            <td className="py-1.5 px-2 font-mono">{s.buildName}</td>
                            <td className="py-1.5 px-2">{s.failedStepName || '�'}</td>
                            <td className="py-1.5 px-2 text-red-300">{s.errorType || '�'}</td>
                            <td className="py-1.5 px-2 font-mono truncate max-w-[300px]" title={s.normalizedMessage}>
                              {s.normalizedMessage}
                            </td>
                            <td className="py-1.5 px-2">{s.agent || '�'}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </div>
              )}

              {/* Suggested action */}
              {report.suggestedAction && (
                <div className="bg-blue-500/10 border border-blue-500/30 rounded p-3">
                  <div className="text-[10px] font-bold uppercase text-blue-300 mb-1">Suggested action</div>
                  <p className="text-sm text-text-primary whitespace-pre-line">{report.suggestedAction}</p>
                </div>
              )}
            </>
          )}
        </div>

        <div className="flex items-center justify-end gap-2 px-5 py-3 border-t border-bdr">
          {latestBuild && onViewLog && (
            <button
              onClick={() => onViewLog(latestBuild, latestFailedStep)}
              className="flex items-center gap-1.5 px-3 py-1.5 bg-blue-600 hover:bg-blue-500 text-white text-sm rounded"
            >
              <FileText size={14} />
              View build {latestBuild} log ?
            </button>
          )}
          <button
            onClick={onClose}
            className="px-3 py-1.5 bg-white/5 hover:bg-white/10 text-text-primary text-sm rounded"
          >
            Close
          </button>
        </div>
      </div>
    </div>
  );
}
