import { useState, useEffect, useMemo } from 'react';
import { useResultsStore } from '../../stores/resultsStore';
import { useResults } from '../../hooks/useResults';
import { useCan, useDisabledReason } from '../../hooks/useCapabilities';
import { logCatch } from '../../lib/logger';
import { Download, Mail, ChevronDown, ChevronRight, Search, Filter, Activity, FileSearch, ExternalLink } from 'lucide-react';
import type { BuildDetailTest } from '../../types/api';
import FailureAnalysisDialog from './FailureAnalysisDialog';
import ExecutionLogDialog from './ExecutionLogDialog';
import { openTraceWindow } from '../../lib/traceWindow';

type DetailTab = 'usecases' | 'failed' | 'all';

export default function BuildDetail() {
  const build = useResultsStore(s => s.selectedBuild);
  const detail = useResultsStore(s => s.buildDetail);
  const { exportReport, sendReport, fetchBuildDetail } = useResults();
  const canSend = useCan('Report_Generate');
  const sendDeniedReason = useDisabledReason('Report_Generate');
  const [activeTab, setActiveTab] = useState<DetailTab>('usecases');
  const [searchText, setSearchText] = useState('');
  const [outcomeFilter, setOutcomeFilter] = useState('All');
  const [expandedUseCase, setExpandedUseCase] = useState<string | null>(null);
  const [expandedTest, setExpandedTest] = useState<string | null>(null);
  const [analysisTarget, setAnalysisTarget] = useState<string | null>(null);
  const [logTarget, setLogTarget] = useState<{ build: string; testName: string; stepIndex?: number } | null>(null);

  // Fetch detail when a build is selected
  useEffect(() => {
    if (build) {
      fetchBuildDetail(build.buildNumber).catch(logCatch('BuildDetail', 'fetchBuildDetail'));
    }
  }, [build, fetchBuildDetail]);

  const filteredTests = useMemo(() => {
    if (!detail) return [];
    let tests = activeTab === 'failed'
      ? detail.tests.filter(t => t.outcome === 'Failed')
      : detail.tests;

    if (outcomeFilter !== 'All')
      tests = tests.filter(t => t.outcome === outcomeFilter);

    if (searchText) {
      const lower = searchText.toLowerCase();
      tests = tests.filter(t =>
        t.testName.toLowerCase().includes(lower) ||
        t.className.toLowerCase().includes(lower) ||
        (t.errorMessage?.toLowerCase().includes(lower) ?? false));
    }
    return tests;
  }, [detail, activeTab, outcomeFilter, searchText]);

  if (!build) return null;

  const healthColor =
    build.health === 'Good' ? 'bg-acc-green text-white'
    : build.health === 'Warning' ? 'bg-acc-yellow text-bg'
    : build.health === 'Bad' ? 'bg-acc-red text-white'
    : 'bg-bg-surface text-text-primary';

  return (
    <div className="space-y-3">
      {/* Header */}
      <div className="flex items-center gap-3">
        <h2 className="text-base font-semibold text-text-primary">Build: {build.buildNumber}</h2>
        <span className={`text-xs px-2 py-0.5 rounded font-bold ${healthColor}`}>
          {build.passRate.toFixed(1)}% · {build.health}
        </span>
        <div className="flex-1" />
        <button
          className="flex items-center gap-1 px-2 py-1 rounded text-xs bg-white/5 text-text-secondary hover:bg-white/10"
          onClick={() => exportReport(build.buildNumber, 'html')}
        >
          <Download size={12} /> HTML
        </button>
        <button
          className="flex items-center gap-1 px-2 py-1 rounded text-xs bg-white/5 text-text-secondary hover:bg-white/10"
          onClick={() => exportReport(build.buildNumber, 'csv')}
        >
          <Download size={12} /> CSV
        </button>
        <div className="relative group inline-block">
          <button
            className={`flex items-center gap-1 px-2 py-1 rounded text-xs ${
              canSend
                ? 'bg-accent/15 text-accent hover:bg-accent/25'
                : 'bg-white/5 text-text-secondary cursor-not-allowed opacity-50'
            }`}
            onClick={() => canSend && sendReport(build.buildNumber)}
            disabled={!canSend}
          >
            <Mail size={12} /> Email
          </button>
          {!canSend && sendDeniedReason && (
            <div className="absolute bottom-full left-1/2 -translate-x-1/2 mb-2 px-3 py-1.5 rounded bg-bg-ribbon border border-bdr text-xs text-text-secondary whitespace-nowrap opacity-0 group-hover:opacity-100 transition-opacity pointer-events-none z-50">
              {sendDeniedReason}
            </div>
          )}
        </div>
      </div>

      {/* KPI cards */}
      <div className="grid grid-cols-5 gap-3">
        <KpiCard label="Total" value={build.totalTests} color="text-accent" />
        <KpiCard label="Passed" value={build.passedTests} color="text-acc-green" />
        <KpiCard label="Failed" value={build.failedTests} color="text-acc-red" />
        <KpiCard label="Timeout" value={build.timeoutTests} color="text-acc-yellow" />
        <KpiCard label="Pass Rate" value={`${build.passRate.toFixed(1)}%`}
          color={build.passRate >= 95 ? 'text-acc-green' : build.passRate >= 85 ? 'text-acc-yellow' : 'text-acc-red'} />
      </div>

      {/* Tabs */}
      <div className="flex items-center gap-1 border-b border-bdr/30">
        {([
          { id: 'usecases' as DetailTab, label: 'By UseCase', count: build.useCases.length },
          { id: 'failed' as DetailTab, label: 'Failed Only', count: build.failedTests },
          { id: 'all' as DetailTab, label: 'All Tests', count: build.totalTests },
        ]).map(tab => (
          <button key={tab.id} onClick={() => { setActiveTab(tab.id); setExpandedTest(null); }}
            className={`px-3 py-1.5 text-xs font-medium border-b-2 transition-colors
              ${activeTab === tab.id
                ? 'border-accent text-accent'
                : 'border-transparent text-text-muted hover:text-text-secondary'}`}>
            {tab.label}
            <span className="ml-1.5 opacity-60">({tab.count})</span>
          </button>
        ))}
      </div>

      {/* Search + Filter (for failed/all tabs) */}
      {(activeTab === 'failed' || activeTab === 'all') && (
        <div className="flex items-center gap-2">
          <div className="relative flex-1">
            <Search size={12} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-text-muted" />
            <input type="text" value={searchText} onChange={e => setSearchText(e.target.value)}
              placeholder="Search test name, class, or error..."
              className="w-full bg-bg-card border border-bdr/30 rounded pl-7 pr-3 py-1.5 text-xs text-text-primary placeholder:text-text-muted focus:border-accent/50 focus:outline-none" />
          </div>
          <div className="relative">
            <Filter size={12} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-text-muted" />
            <select value={outcomeFilter} onChange={e => setOutcomeFilter(e.target.value)}
              className="bg-bg-card border border-bdr/30 rounded pl-7 pr-6 py-1.5 text-xs text-text-primary appearance-none cursor-pointer focus:border-accent/50 focus:outline-none">
              <option value="All">All Outcomes</option>
              <option value="Failed">Failed</option>
              <option value="Passed">Passed</option>
              <option value="NotExecuted">Skipped</option>
            </select>
          </div>
          <span className="text-[10px] text-text-muted">{filteredTests.length} tests</span>
        </div>
      )}

      {/* UseCase Tab */}
      {activeTab === 'usecases' && (
        <div className="space-y-1.5">
          {build.useCases.map(uc => (
            <div key={uc.useCaseName} className="bg-bg-card rounded-lg overflow-hidden">
              <div className="flex items-center justify-between px-3 py-2 cursor-pointer hover:bg-white/5"
                onClick={() => setExpandedUseCase(expandedUseCase === uc.useCaseName ? null : uc.useCaseName)}>
                <div className="flex items-center gap-2">
                  {expandedUseCase === uc.useCaseName ? <ChevronDown size={12} className="text-text-muted" /> : <ChevronRight size={12} className="text-text-muted" />}
                  <span className="text-xs font-medium text-text-primary">{uc.useCaseName}</span>
                  <span className="text-[10px] text-text-muted">{uc.total} tests</span>
                </div>
                <div className="flex items-center gap-3 text-[10px]">
                  <span className="text-acc-green">{uc.passed} pass</span>
                  {uc.failed > 0 && <span className="text-acc-red font-bold">{uc.failed} fail</span>}
                  <span className={`font-bold ${uc.passRate >= 95 ? 'text-acc-green' : uc.passRate >= 85 ? 'text-acc-yellow' : 'text-acc-red'}`}>
                    {uc.passRate.toFixed(1)}%
                  </span>
                  <div className="w-16 h-1 bg-bg rounded-full overflow-hidden">
                    <div className="h-full bg-acc-green rounded-full" style={{ width: `${uc.passRate}%` }} />
                  </div>
                </div>
              </div>
              {expandedUseCase === uc.useCaseName && detail && (
                <div className="border-t border-bdr/20">
                  {detail.tests.filter(t => t.useCase === uc.useCaseName).map((test, i) => (
                    <TestRow key={`${test.testName}-${i}`} test={test}
                      expanded={expandedTest === `${uc.useCaseName}/${test.testName}/${i}`}
                      onToggle={() => setExpandedTest(expandedTest === `${uc.useCaseName}/${test.testName}/${i}` ? null : `${uc.useCaseName}/${test.testName}/${i}`)}
                      onAnalyze={() => setAnalysisTarget(test.testName)}
                      onViewLog={() => build && setLogTarget({ build: build.buildNumber, testName: test.testName })} />
                  ))}
                </div>
              )}
            </div>
          ))}
        </div>
      )}

      {/* Failed / All Tests Tab */}
      {(activeTab === 'failed' || activeTab === 'all') && (
        <div className="bg-bg-card rounded-lg overflow-hidden">
          {filteredTests.length > 0 ? filteredTests.map((test, i) => (
            <TestRow key={`${test.testName}-${i}`} test={test}
              expanded={expandedTest === `flat/${test.testName}/${i}`}
              onToggle={() => setExpandedTest(expandedTest === `flat/${test.testName}/${i}` ? null : `flat/${test.testName}/${i}`)}
              onAnalyze={() => setAnalysisTarget(test.testName)}
              onViewLog={() => build && setLogTarget({ build: build.buildNumber, testName: test.testName })} />
          )) : (
            <div className="px-3 py-6 text-center text-xs text-text-muted">
              {searchText ? 'No tests match your search' : 'No tests to display'}
            </div>
          )}
        </div>
      )}

      {/* Original failed tests fallback when detail hasn't loaded */}
      {!detail && build.allFailedTests.length > 0 && (
        <div className="bg-bg-card rounded-lg overflow-hidden">
          <div className="px-3 py-2 border-b border-bdr text-xs font-semibold text-acc-red uppercase">
            Failed Tests ({build.allFailedTests.length})
          </div>
          <div className="max-h-64 overflow-auto">
            {build.allFailedTests.map((t, i) => (
              <div key={i} className="px-3 py-2 border-b border-bdr/30 text-xs">
                <div className="flex items-center gap-2">
                  <span className="font-medium text-acc-red flex-1">{t.testName}</span>
                  <button
                    className="flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px] bg-red-500/10 text-red-400 hover:bg-red-500/20"
                    onClick={() => setAnalysisTarget(t.testName)}
                    title="Analyze failure pattern"
                  >
                    <Activity size={10} /> Analyze
                  </button>
                </div>
                <div className="text-text-muted">{t.useCaseName} · {t.trxFileName}</div>
                {t.errorMessage && (
                  <div className="mt-1 text-text-secondary bg-bg-panel rounded p-1.5 whitespace-pre-wrap break-all max-h-20 overflow-auto">
                    {t.errorMessage}
                  </div>
                )}
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Failure Analysis Dialog */}
      {analysisTarget && (
        <FailureAnalysisDialog
          testName={analysisTarget}
          onClose={() => setAnalysisTarget(null)}
          onViewLog={(buildName, stepIndex) => {
            setAnalysisTarget(null);
            setLogTarget({ build: buildName, testName: analysisTarget, stepIndex });
          }}
        />
      )}

      {/* Execution Log Dialog */}
      {logTarget && (
        <ExecutionLogDialog
          build={logTarget.build}
          testName={logTarget.testName}
          stepIndex={logTarget.stepIndex}
          onClose={() => setLogTarget(null)}
        />
      )}
    </div>
  );
}

function TestRow({ test, expanded, onToggle, onAnalyze, onViewLog }: {
  test: BuildDetailTest; expanded: boolean; onToggle: () => void;
  onAnalyze?: () => void; onViewLog?: () => void;
}) {
  const outcomeIcon = test.outcome === 'Passed' ? '✔' : test.outcome === 'Failed' ? '✖' : '○';
  const outcomeColor = test.outcome === 'Passed' ? 'text-acc-green' : test.outcome === 'Failed' ? 'text-acc-red' : 'text-acc-yellow';

  return (
    <div className="border-b border-bdr/20 last:border-b-0">
      <div className="flex items-center px-3 py-1.5 hover:bg-white/5 cursor-pointer" onClick={onToggle}>
        <span className={`w-5 text-[10px] ${outcomeColor}`}>{outcomeIcon}</span>
        <span className="flex-1 text-xs text-text-primary truncate">{test.testName}</span>
        {test.trxFile && <span className="text-[10px] text-text-muted mx-2 truncate max-w-[120px]">{test.trxFile}</span>}
        <span className="text-[10px] text-text-muted w-14 text-right">{test.durationText}</span>
      </div>
      {expanded && (
        <div className="px-8 pb-3 space-y-2">
          {test.className && (
            <div className="text-[10px] text-text-muted">Class: {test.className}</div>
          )}
          {test.outcome === 'Failed' && (
            <div className="flex items-center gap-2">
              {onAnalyze && (
                <button
                  className="flex items-center gap-1 px-2 py-1 rounded text-[10px] bg-red-500/10 text-red-400 hover:bg-red-500/20"
                  onClick={e => { e.stopPropagation(); onAnalyze(); }}
                >
                  <Activity size={10} /> Analyze Failure Pattern
                </button>
              )}
              {onViewLog && (
                <button
                  className="flex items-center gap-1 px-2 py-1 rounded text-[10px] bg-blue-500/10 text-blue-400 hover:bg-blue-500/20"
                  onClick={e => { e.stopPropagation(); onViewLog(); }}
                >
                  <FileSearch size={10} /> View Execution Log
                </button>
              )}
              <button
                className="flex items-center gap-1 px-2 py-1 rounded text-[10px] bg-purple-500/10 text-purple-400 hover:bg-purple-500/20"
                title="Open the full debug trace in a separate window"
                onClick={e => { e.stopPropagation(); openTraceWindow(test); }}
              >
                <ExternalLink size={10} /> Pop Out Trace
              </button>
            </div>
          )}
          {test.errorMessage && (
            <div>
              <div className="text-[10px] text-acc-red font-semibold uppercase tracking-wider mb-0.5">Error Message</div>
              <pre className="text-[11px] text-acc-red/80 bg-acc-red/5 rounded p-2 overflow-x-auto whitespace-pre-wrap max-h-32">{test.errorMessage}</pre>
            </div>
          )}
          {test.stackTrace && (
            <div>
              <div className="text-[10px] text-text-muted font-semibold uppercase tracking-wider mb-0.5">Stack Trace</div>
              <pre className="text-[10px] text-text-secondary bg-bg rounded p-2 overflow-x-auto whitespace-pre-wrap max-h-48 font-mono">{test.stackTrace}</pre>
            </div>
          )}
          {test.debugTrace && (
            <details className="text-[10px]">
              <summary className="text-text-muted cursor-pointer hover:text-text-secondary">Debug Trace ({test.debugTrace.length} chars)</summary>
              <pre className="mt-1 text-[10px] text-text-muted bg-bg rounded p-2 overflow-x-auto max-h-32 font-mono">{test.debugTrace}</pre>
            </details>
          )}
          {test.stdOut && (
            <details className="text-[10px]">
              <summary className="text-text-muted cursor-pointer hover:text-text-secondary">Stdout ({test.stdOut.length} chars)</summary>
              <pre className="mt-1 text-[10px] text-text-muted bg-bg rounded p-2 overflow-x-auto max-h-32 font-mono">{test.stdOut}</pre>
            </details>
          )}
          {test.steps && test.steps.length > 0 && (
            <details className="text-[10px]">
              <summary className="text-text-muted cursor-pointer hover:text-text-secondary">Execution Steps ({test.steps.length})</summary>
              <div className="mt-1 space-y-1">
                {test.steps.map((step, si) => (
                  <div key={si} className="flex items-center gap-2 px-2 py-1 bg-bg rounded text-[10px]">
                    <span className={step.outcome === 'Passed' ? 'text-acc-green' : step.outcome === 'Failed' ? 'text-acc-red' : 'text-acc-yellow'}>
                      {step.outcome === 'Passed' ? '✔' : step.outcome === 'Failed' ? '✖' : '○'}
                    </span>
                    <span className="text-text-primary truncate flex-1">{step.stepName}</span>
                    {step.errorMessage && <span className="text-acc-red truncate max-w-[200px]">{step.errorMessage}</span>}
                  </div>
                ))}
              </div>
            </details>
          )}
        </div>
      )}
    </div>
  );
}

function KpiCard({ label, value, color }: { label: string; value: number | string; color: string }) {
  return (
    <div className="bg-bg-card rounded-lg p-3 text-center">
      <div className={`text-2xl font-bold ${color}`}>{value}</div>
      <div className="text-[10px] text-text-muted uppercase tracking-wider mt-1">{label}</div>
    </div>
  );
}
