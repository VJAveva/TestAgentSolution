import { useEffect, useMemo, useState } from 'react';
import { X, Copy } from 'lucide-react';
import { apiFetch } from '../../lib/api';
import type { MergedLogLine, ExecutionLogReport } from '../../types/api';

type MergedLine = MergedLogLine;
type LogReport = ExecutionLogReport;

interface Props {
  build: string;
  testName: string;
  stepIndex?: number;
  onClose: () => void;
}

const SOURCE_BADGE: Record<string, string> = {
  TRX: 'bg-blue-600',
  Agent: 'bg-green-600',
  Controller: 'bg-purple-600',
};

export default function ExecutionLogDialog({ build, testName, stepIndex, onClose }: Props) {
  const [report, setReport] = useState<LogReport | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [showAll, setShowAll] = useState(true);
  const [showTrx, setShowTrx] = useState(true);
  const [showAgent, setShowAgent] = useState(true);
  const [showController, setShowController] = useState(true);
  const [errorsOnly, setErrorsOnly] = useState(false);
  const [search, setSearch] = useState('');

  useEffect(() => {
    const url = `/api/results/builds/${encodeURIComponent(build)}/test/${encodeURIComponent(testName)}/log` +
                (stepIndex !== undefined ? `?stepIndex=${stepIndex}` : '');
    apiFetch<LogReport>(url)
      .then(setReport)
      .catch(err => setError(err?.message ?? 'Failed to load log'));
  }, [build, testName, stepIndex]);

  const filtered = useMemo(() => {
    if (!report) return [];
    return report.mergedTimeline.filter(l => {
      if (l.source === 'TRX' && !showTrx) return false;
      if (l.source === 'Agent' && !showAgent) return false;
      if (l.source === 'Controller' && !showController) return false;
      if (errorsOnly && l.severity !== 'Error') return false;
      if (search && !l.message.toLowerCase().includes(search.toLowerCase())) return false;
      return true;
    });
  }, [report, showTrx, showAgent, showController, errorsOnly, search]);

  const handleCopy = () => {
    const text = filtered.map(l =>
      `[${new Date(l.timestamp).toLocaleTimeString()}] ${l.source.padEnd(10)} ${l.severity.padEnd(8)} ${l.message}`
    ).join('\n');
    navigator.clipboard?.writeText(text);
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60 backdrop-blur-sm">
      <div className="bg-bg-panel rounded-lg border border-bdr w-[1100px] h-[85vh] flex flex-col">
        <div className="flex items-center justify-between px-5 py-3 border-b border-bdr">
          <div>
            <h2 className="text-lg font-bold text-text-primary">Execution Log Viewer</h2>
            <p className="text-xs text-text-muted mt-1 font-mono">{testName}</p>
          </div>
          <button onClick={onClose} className="p-1.5 hover:bg-white/5 rounded">
            <X size={18} className="text-text-muted" />
          </button>
        </div>

        {error && (
          <div className="bg-red-500/10 border-b border-red-500/30 p-3 text-red-300 text-sm">
            {error}
          </div>
        )}

        {report && (
          <>
            {/* Context strip */}
            <div className="grid grid-cols-6 gap-3 px-5 py-3 bg-white/5 border-b border-bdr text-xs">
              <div><div className="text-[10px] text-text-muted uppercase">Build</div><div className="font-bold">{report.buildName}</div></div>
              <div><div className="text-[10px] text-text-muted uppercase">Agent</div><div className="font-bold">{report.agent || '�'}</div></div>
              <div><div className="text-[10px] text-text-muted uppercase">Outcome</div>
                <div className={`font-bold ${report.outcome === 'Passed' ? 'text-green-400' : report.outcome === 'Failed' ? 'text-red-400' : ''}`}>
                  {report.outcome}
                </div>
              </div>
              <div><div className="text-[10px] text-text-muted uppercase">Duration</div><div className="font-bold">{report.duration}</div></div>
              <div><div className="text-[10px] text-text-muted uppercase">Started</div><div className="text-[11px]">{report.startTime}</div></div>
              <div><div className="text-[10px] text-text-muted uppercase">Failed step</div>
                <div className="font-bold text-red-400">
                  {report.failedStepIndex >= 0 ? `Step #${report.failedStepIndex}` : '�'}
                </div>
              </div>
            </div>

            {/* Filter pills */}
            <div className="flex items-center gap-2 px-5 py-2 bg-white/5 border-b border-bdr">
              <span className="text-xs text-text-muted">Source:</span>
              <FilterPill label="All" checked={showAll} onClick={() => {
                setShowAll(true); setShowTrx(true); setShowAgent(true); setShowController(true);
              }} />
              <FilterPill label="TRX" checked={showTrx} onClick={() => setShowTrx(v => !v)} />
              <FilterPill label="Agent" checked={showAgent} onClick={() => setShowAgent(v => !v)} />
              <FilterPill label="Controller" checked={showController} onClick={() => setShowController(v => !v)} />
              <div className="w-px h-5 bg-bdr mx-2" />
              <FilterPill label="Errors only" checked={errorsOnly} onClick={() => setErrorsOnly(v => !v)} />
              <input
                type="text"
                value={search}
                onChange={e => setSearch(e.target.value)}
                placeholder="Filter messages�"
                className="ml-auto px-2 py-1 bg-bg border border-bdr rounded text-xs w-64 text-text-primary"
              />
            </div>

            {/* Timeline */}
            <div className="flex-1 overflow-auto bg-bg p-2 font-mono text-xs">
              {filtered.map((l, i) => (
                <div key={i} className="flex gap-2 py-0.5 hover:bg-white/5 px-2">
                  <span className="text-text-muted shrink-0 w-20">{new Date(l.timestamp).toLocaleTimeString()}</span>
                  <span className={`shrink-0 w-16 text-center text-[10px] font-bold text-white px-1 rounded ${SOURCE_BADGE[l.source] ?? 'bg-slate-600'}`}>
                    {l.source}
                  </span>
                  <span className={`shrink-0 w-14 ${l.severity === 'Error' ? 'text-red-400' : l.severity === 'Warning' ? 'text-amber-400' : 'text-text-muted'}`}>
                    {l.severity}
                  </span>
                  <span className="text-text-primary break-all whitespace-pre-wrap">{l.message}</span>
                </div>
              ))}
              {filtered.length === 0 && (
                <div className="text-center text-text-muted py-12">No log lines match the current filter.</div>
              )}
            </div>

            {/* Error block */}
            {report.errorMessage && (
              <div className="border-t border-red-500/30 bg-red-500/5 p-3 max-h-40 overflow-auto">
                <div className="text-[10px] font-bold text-red-400 uppercase mb-1">Error</div>
                <div className="text-xs text-text-primary font-mono whitespace-pre-wrap">{report.errorMessage}</div>
                {report.stackTrace && (
                  <div className="text-[10px] text-text-muted font-mono whitespace-pre-wrap mt-2">{report.stackTrace}</div>
                )}
              </div>
            )}

            <div className="flex items-center justify-between px-5 py-2 border-t border-bdr">
              <span className="text-xs text-text-muted">{filtered.length} of {report.mergedTimeline.length} lines</span>
              <div className="flex gap-2">
                <button onClick={handleCopy} className="flex items-center gap-1 px-3 py-1 bg-white/5 hover:bg-white/10 text-sm rounded">
                  <Copy size={12} /> Copy
                </button>
                <button onClick={onClose} className="px-3 py-1 bg-white/5 hover:bg-white/10 text-sm rounded">Close</button>
              </div>
            </div>
          </>
        )}
      </div>
    </div>
  );
}

function FilterPill({ label, checked, onClick }: { label: string; checked: boolean; onClick: () => void }) {
  return (
    <button
      onClick={onClick}
      className={`px-2.5 py-1 rounded text-xs font-medium transition-colors ${
        checked ? 'bg-accent/20 text-accent' : 'bg-white/5 text-text-muted hover:bg-white/10'
      }`}
    >
      {label}
    </button>
  );
}
