import { useResultsStore } from '../../stores/resultsStore';
import { useResults } from '../../hooks/useResults';
import { Download, Mail } from 'lucide-react';

export default function BuildDetail() {
  const build = useResultsStore(s => s.selectedBuild);
  const { exportReport, sendReport } = useResults();

  if (!build) return null;

  const healthColor =
    build.health === 'Good' ? 'bg-acc-green text-white'
    : build.health === 'Warning' ? 'bg-acc-yellow text-bg'
    : build.health === 'Bad' ? 'bg-acc-red text-white'
    : 'bg-bg-surface text-text-primary';

  return (
    <div className="space-y-3">
      <div className="flex items-center gap-3">
        <h2 className="text-base font-semibold text-text-primary">Build: {build.buildNumber}</h2>
        <span className={`text-xs px-2 py-0.5 rounded font-bold ${healthColor}`}>
          {build.passRate.toFixed(1)}% — {build.health}
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
        <button
          className="flex items-center gap-1 px-2 py-1 rounded text-xs bg-accent/15 text-accent hover:bg-accent/25"
          onClick={() => sendReport(build.buildNumber)}
        >
          <Mail size={12} /> Email
        </button>
      </div>

      {/* KPI cards */}
      <div className="grid grid-cols-4 gap-3">
        <KpiCard label="Total" value={build.totalTests} color="text-accent" />
        <KpiCard label="Passed" value={build.passedTests} color="text-acc-green" />
        <KpiCard label="Failed" value={build.failedTests} color="text-acc-red" />
        <KpiCard label="Timeout" value={build.timeoutTests} color="text-acc-yellow" />
      </div>

      {/* Use Case breakdown */}
      <div className="bg-bg-card rounded-lg overflow-hidden">
        <table className="w-full text-xs">
          <thead>
            <tr className="text-left text-text-muted uppercase">
              <th className="px-3 py-2">Use Case</th>
              <th className="px-3 py-2 text-right">Total</th>
              <th className="px-3 py-2 text-right">Passed</th>
              <th className="px-3 py-2 text-right">Failed</th>
              <th className="px-3 py-2 text-right">Pass Rate</th>
            </tr>
          </thead>
          <tbody>
            {build.useCases.map(uc => (
              <tr key={uc.useCaseName} className="border-t border-bdr/30 hover:bg-white/5">
                <td className="px-3 py-1.5 font-medium text-text-primary">{uc.useCaseName}</td>
                <td className="px-3 py-1.5 text-right">{uc.total}</td>
                <td className="px-3 py-1.5 text-right text-acc-green">{uc.passed}</td>
                <td className={`px-3 py-1.5 text-right ${uc.failed > 0 ? 'text-acc-red font-medium' : ''}`}>{uc.failed}</td>
                <td className={`px-3 py-1.5 text-right font-medium ${uc.passRate > 95 ? 'text-acc-green' : uc.passRate >= 85 ? 'text-acc-yellow' : 'text-acc-red'}`}>
                  {uc.passRate.toFixed(1)}%
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {/* Failed tests */}
      {build.allFailedTests.length > 0 && (
        <div className="bg-bg-card rounded-lg overflow-hidden">
          <div className="px-3 py-2 border-b border-bdr text-xs font-semibold text-acc-red uppercase">
            Failed Tests ({build.allFailedTests.length})
          </div>
          <div className="max-h-64 overflow-auto">
            {build.allFailedTests.map((t, i) => (
              <div key={i} className="px-3 py-2 border-b border-bdr/30 text-xs">
                <div className="font-medium text-acc-red">{t.testName}</div>
                <div className="text-text-muted">{t.useCaseName} — {t.trxFileName}</div>
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
    </div>
  );
}

function KpiCard({ label, value, color }: { label: string; value: number; color: string }) {
  return (
    <div className="bg-bg-card rounded-lg p-3 text-center">
      <div className={`text-2xl font-bold ${color}`}>{value}</div>
      <div className="text-[10px] text-text-muted uppercase tracking-wider mt-1">{label}</div>
    </div>
  );
}
