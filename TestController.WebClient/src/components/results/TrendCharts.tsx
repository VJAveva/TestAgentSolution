import { useEffect } from 'react';
import { useResultsStore } from '../../stores/resultsStore';
import { useResults } from '../../hooks/useResults';
import { logCatch } from '../../lib/logger';
import {
  ResponsiveContainer, LineChart, Line, BarChart, Bar, PieChart, Pie, Cell,
  XAxis, YAxis, CartesianGrid, Tooltip, Legend, ReferenceLine,
} from 'recharts';

const CHART_COLORS = {
  green: '#A6E3A1',
  red: '#F38BA8',
  yellow: '#F9E2AF',
  blue: '#89B4FA',
  mauve: '#CBA6F7',
  peach: '#FAB387',
  muted: '#585B70',
  grid: '#313244',
  text: '#9399B2',
};

export default function TrendCharts() {
  const trends = useResultsStore(s => s.trends);
  const alerts = useResultsStore(s => s.alerts);
  const { fetchTrends, fetchAlerts } = useResults();

  useEffect(() => {
    fetchTrends().catch(logCatch('TrendCharts', 'fetchTrends'));
    fetchAlerts().catch(logCatch('TrendCharts', 'fetchAlerts'));
  }, [fetchTrends, fetchAlerts]);

  return (
    <div className="space-y-4">
      {/* Pass Rate Line Chart */}
      {trends && trends.builds.length > 0 && (
        <div className="bg-bg-card rounded-lg p-4">
          <h3 className="text-xs font-semibold text-text-secondary uppercase mb-3">Pass Rate Trend</h3>
          <ResponsiveContainer width="100%" height={220}>
            <LineChart data={trends.builds} margin={{ top: 5, right: 20, bottom: 5, left: 0 }}>
              <CartesianGrid strokeDasharray="3 3" stroke={CHART_COLORS.grid} />
              <XAxis dataKey="buildNumber" tick={{ fill: CHART_COLORS.text, fontSize: 10 }} interval="preserveStartEnd" />
              <YAxis domain={[0, 100]} tick={{ fill: CHART_COLORS.text, fontSize: 10 }} tickFormatter={(v: number) => `${v}%`} />
              <Tooltip
                contentStyle={{ background: '#1E1E2E', border: '1px solid #585B70', borderRadius: 8, fontSize: 11 }}
                labelStyle={{ color: '#CDD6F4' }}
                formatter={(v: number) => [`${v.toFixed(1)}%`, 'Pass Rate']}
              />
              <ReferenceLine y={trends.goodThreshold} stroke={CHART_COLORS.green} strokeDasharray="6 3" strokeWidth={1.5} label={{ value: `Good ${trends.goodThreshold}%`, position: 'right', fill: CHART_COLORS.green, fontSize: 10 }} />
              <ReferenceLine y={trends.warningThreshold} stroke={CHART_COLORS.yellow} strokeDasharray="6 3" strokeWidth={1.5} label={{ value: `Warn ${trends.warningThreshold}%`, position: 'right', fill: CHART_COLORS.yellow, fontSize: 10 }} />
              <Line type="monotone" dataKey="passRate" stroke={CHART_COLORS.blue} strokeWidth={2} dot={{ r: 3, fill: CHART_COLORS.blue }} activeDot={{ r: 5 }} />
            </LineChart>
          </ResponsiveContainer>
          <div className="flex gap-4 mt-2 text-xs text-text-muted">
            <span>{trends.builds.length} builds analyzed</span>
            <span>Avg: {(trends.builds.reduce((s, b) => s + b.passRate, 0) / trends.builds.length).toFixed(1)}%</span>
            <span className="text-acc-green">Good: ≥{trends.goodThreshold}%</span>
            <span className="text-acc-yellow">Warning: ≥{trends.warningThreshold}%</span>
            <span className="text-acc-red">Bad: &lt;{trends.warningThreshold}%</span>
          </div>
        </div>
      )}

      {/* Passed / Failed / Timeout Bar Chart */}
      {trends && trends.builds.length > 0 && (
        <div className="bg-bg-card rounded-lg p-4">
          <h3 className="text-xs font-semibold text-text-secondary uppercase mb-3">Test Counts Per Build</h3>
          <ResponsiveContainer width="100%" height={220}>
            <BarChart data={trends.builds} margin={{ top: 5, right: 20, bottom: 5, left: 0 }}>
              <CartesianGrid strokeDasharray="3 3" stroke={CHART_COLORS.grid} />
              <XAxis dataKey="buildNumber" tick={{ fill: CHART_COLORS.text, fontSize: 10 }} interval="preserveStartEnd" />
              <YAxis tick={{ fill: CHART_COLORS.text, fontSize: 10 }} />
              <Tooltip contentStyle={{ background: '#1E1E2E', border: '1px solid #585B70', borderRadius: 8, fontSize: 11 }} labelStyle={{ color: '#CDD6F4' }} />
              <Legend wrapperStyle={{ fontSize: 11, color: CHART_COLORS.text }} />
              <Bar dataKey="passedTests" stackId="a" fill={CHART_COLORS.green} name="Passed" />
              <Bar dataKey="failedTests" stackId="a" fill={CHART_COLORS.red} name="Failed" />
              <Bar dataKey="timeoutTests" stackId="a" fill={CHART_COLORS.yellow} name="Timeout" />
            </BarChart>
          </ResponsiveContainer>
        </div>
      )}

      {/* Latest Build Outcome Pie Chart */}
      {trends && trends.builds.length > 0 && (() => {
        const latest = trends.builds[trends.builds.length - 1];
        const pieData = [
          { name: 'Passed', value: latest.passedTests, color: CHART_COLORS.green },
          { name: 'Failed', value: latest.failedTests, color: CHART_COLORS.red },
          { name: 'Timeout', value: latest.timeoutTests, color: CHART_COLORS.yellow },
        ].filter(d => d.value > 0);

        return (
          <div className="bg-bg-card rounded-lg p-4">
            <h3 className="text-xs font-semibold text-text-secondary uppercase mb-3">
              Latest Build Breakdown: {latest.buildNumber}
            </h3>
            <div className="flex items-center">
              <ResponsiveContainer width="50%" height={200}>
                <PieChart>
                  <Pie data={pieData} dataKey="value" nameKey="name" cx="50%" cy="50%" outerRadius={80} innerRadius={40} paddingAngle={2}>
                    {pieData.map((entry, idx) => (
                      <Cell key={idx} fill={entry.color} />
                    ))}
                  </Pie>
                  <Tooltip contentStyle={{ background: '#1E1E2E', border: '1px solid #585B70', borderRadius: 8, fontSize: 11 }} />
                </PieChart>
              </ResponsiveContainer>
              <div className="space-y-2 text-xs">
                {pieData.map((d, i) => (
                  <div key={i} className="flex items-center gap-2">
                    <span className="w-3 h-3 rounded-sm" style={{ background: d.color }} />
                    <span className="text-text-primary">{d.name}</span>
                    <span className="text-text-muted ml-auto">{d.value}</span>
                  </div>
                ))}
                <div className="pt-1 border-t border-bdr text-text-muted">
                  Total: {latest.totalTests} &middot; {latest.passRate.toFixed(1)}%
                </div>
              </div>
            </div>
          </div>
        );
      })()}

      {/* Consecutive Failure Alerts */}
      {alerts.length > 0 && (
        <div className="bg-bg-card rounded-lg overflow-hidden">
          <div className="px-3 py-2 border-b border-bdr text-xs font-semibold text-acc-red uppercase">
            ? Consecutive Failure Alerts ({alerts.length})
          </div>
          <div className="max-h-64 overflow-auto">
            {alerts.map((a, i) => {
              const priorityColor =
                a.priority === 'CRITICAL' ? 'bg-acc-red/20 text-acc-red'
                : a.priority === 'HIGH' ? 'bg-acc-peach/20 text-acc-peach'
                : 'bg-acc-yellow/20 text-acc-yellow';
              return (
                <div key={i} className="px-3 py-2 border-b border-bdr/30 text-xs">
                  <div className="flex items-center gap-2">
                    <span className={`text-[10px] px-1.5 py-0.5 rounded font-bold ${priorityColor}`}>{a.priority}</span>
                    <span className="font-medium text-text-primary">{a.testName}</span>
                    <span className="text-text-muted ml-auto">{a.consecutiveFailCount}× consecutive</span>
                  </div>
                  <div className="text-text-muted mt-0.5">{a.useCaseName}</div>
                  {a.lastError && (
                    <div className="mt-1 text-text-secondary truncate">{a.lastError}</div>
                  )}
                </div>
              );
            })}
          </div>
        </div>
      )}

      {/* Weekly/Monthly summaries */}
      {trends && trends.weeklySummaries.length > 0 && (
        <div className="bg-bg-card rounded-lg overflow-hidden">
          <div className="px-3 py-2 border-b border-bdr text-xs font-semibold text-text-secondary uppercase">Weekly Summary</div>
          <table className="w-full text-xs">
            <thead>
              <tr className="text-text-muted">
                <th className="px-3 py-1.5 text-left">Week</th>
                <th className="px-3 py-1.5 text-right">Builds</th>
                <th className="px-3 py-1.5 text-right">Tests</th>
                <th className="px-3 py-1.5 text-right">Passed</th>
                <th className="px-3 py-1.5 text-right">Failed</th>
                <th className="px-3 py-1.5 text-right">Avg Rate</th>
              </tr>
            </thead>
            <tbody>
              {trends.weeklySummaries.map(s => (
                <tr key={s.period} className="border-t border-bdr/30 hover:bg-white/5">
                  <td className="px-3 py-1">{s.period}</td>
                  <td className="px-3 py-1 text-right">{s.buildCount}</td>
                  <td className="px-3 py-1 text-right">{s.totalTests}</td>
                  <td className="px-3 py-1 text-right text-acc-green">{s.totalPassed}</td>
                  <td className={`px-3 py-1 text-right ${s.totalFailed > 0 ? 'text-acc-red' : ''}`}>{s.totalFailed}</td>
                  <td className="px-3 py-1 text-right font-medium">{s.avgPassRate.toFixed(1)}%</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}
