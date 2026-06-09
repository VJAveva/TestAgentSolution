import { useExecutionDashboard } from '../../hooks/useExecutionDashboard';

export function StatsBar() {
  const { activeSessions } = useExecutionDashboard();

  const totalAgents = activeSessions
    .reduce((sum, s) => sum + (s.agents || []).length, 0);
  const totalPassed = activeSessions
    .reduce((sum, s) => sum + (s.passedActions ?? 0), 0);
  const totalFailed = activeSessions
    .reduce((sum, s) => sum + (s.failedActions ?? 0), 0);
  const overallProgress = activeSessions.length > 0
    ? Math.round(activeSessions
        .reduce((sum, s) => sum + (s.progressPercent ?? 0), 0)
        / activeSessions.length)
    : 0;

  const stats = [
    { value: activeSessions.length, label: 'Active sessions', color: 'text-accent' },
    { value: totalAgents, label: 'Agents locked', color: 'text-text-primary' },
    { value: totalPassed, label: 'Actions passed', color: 'text-acc-green' },
    { value: totalFailed, label: 'Actions failed', color: 'text-acc-red' },
    { value: overallProgress + '%', label: 'Overall progress', color: 'text-text-primary' },
  ];

  return (
    <div className="grid gap-2 px-4 py-3" style={{ gridTemplateColumns: `repeat(${stats.length}, 1fr)` }}>
      {stats.map((s, i) => (
        <div key={i} className="bg-bg-surface rounded-lg p-2.5 text-center">
          <div className={`text-xl font-medium ${s.color}`}>{s.value}</div>
          <div className="text-[11px] text-text-muted mt-0.5">{s.label}</div>
        </div>
      ))}
    </div>
  );
}
