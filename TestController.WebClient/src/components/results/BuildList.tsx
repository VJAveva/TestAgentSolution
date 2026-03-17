import { useEffect } from 'react';
import { useResultsStore } from '../../stores/resultsStore';
import { useResults } from '../../hooks/useResults';

export default function BuildList() {
  const builds = useResultsStore(s => s.builds);
  const selectedBuild = useResultsStore(s => s.selectedBuild);
  const { fetchBuilds, fetchBuild } = useResults();

  useEffect(() => { fetchBuilds().catch(() => {}); }, [fetchBuilds]);

  return (
    <div className="flex flex-col">
      <div className="px-3 py-2 border-b border-bdr">
        <span className="text-xs text-text-muted">{builds.length} build(s)</span>
      </div>
      <div className="flex-1 overflow-auto">
        {builds.length === 0 && <p className="p-3 text-xs text-text-muted">No builds found.</p>}
        {builds.map(b => {
          const isSelected = selectedBuild?.buildNumber === b.buildNumber;
          const healthColor =
            b.health === 'Good' ? 'text-acc-green bg-acc-green/15'
            : b.health === 'Warning' ? 'text-acc-yellow bg-acc-yellow/15'
            : b.health === 'Bad' ? 'text-acc-red bg-acc-red/15'
            : 'text-text-muted bg-white/5';
          return (
            <div
              key={b.buildNumber}
              className={`px-3 py-2 cursor-pointer border-b border-bdr/50 text-xs transition-colors
                ${isSelected ? 'bg-accent/15 text-accent' : 'hover:bg-white/5 text-text-primary'}`}
              onClick={() => fetchBuild(b.buildNumber)}
            >
              <div className="flex items-center justify-between">
                <span className="font-medium truncate">{b.buildNumber}</span>
                <span className={`text-[10px] px-1.5 py-0.5 rounded font-medium ${healthColor}`}>{b.health}</span>
              </div>
              <div className="flex gap-3 mt-0.5 text-text-muted">
                <span>{b.passRate.toFixed(1)}%</span>
                <span className="text-acc-green">{b.passedTests} ?</span>
                {b.failedTests > 0 && <span className="text-acc-red">{b.failedTests} ?</span>}
                <span>{b.totalTests} total</span>
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}
