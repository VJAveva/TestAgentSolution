import { useMemo, useRef, useEffect, useState } from 'react';
import { useExecutionDashboard } from '../../hooks/useExecutionDashboard';
import { StatsBar } from './StatsBar';
import type { ActionExecution, AgentExecution, SessionSummary } from '../../types/execution';

const statusColors: Record<string, { bg: string; text: string }> = {
  Pending: { bg: 'bg-gray-700', text: 'text-gray-400' },
  Running: { bg: 'bg-blue-900/60', text: 'text-blue-400' },
  Success: { bg: 'bg-green-900/60', text: 'text-green-400' },
  Failed:  { bg: 'bg-red-900/60', text: 'text-red-400' },
  Skipped: { bg: 'bg-gray-800', text: 'text-gray-500' },
};

interface TimelineBar {
  agentName: string;
  actionTag: string;
  sessionTag: string;
  status: string;
  offsetPercent: number;
  widthPercent: number;
  lane: number; // stacked lane index for overlap
}

export default function TimelineView() {
  const { activeSessions, completedSessions } = useExecutionDashboard();
  const containerRef = useRef<HTMLDivElement>(null);
  const [now, setNow] = useState(Date.now());

  // Refresh cursor every second
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(id);
  }, []);

  const allSessions = useMemo(
    () => [...activeSessions, ...completedSessions],
    [activeSessions, completedSessions]
  );

  // Compute time origin and total span
  const { timeOrigin, totalMs, agentBars, cursorPercent } = useMemo(() => {
    let earliest = Infinity;
    const bars: TimelineBar[] = [];

    for (const session of allSessions) {
      const sessionStart = new Date(session.startedUtc).getTime();
      if (sessionStart < earliest) earliest = sessionStart;

      for (const agent of session.agents) {
        for (const action of agent.actions) {
          const startStr = (action as any).startedUtc;
          const startMs = startStr ? new Date(startStr).getTime() : sessionStart;
          if (startMs < earliest) earliest = startMs;
        }
      }
    }

    if (earliest === Infinity) earliest = Date.now();
    const total = Math.max((now - earliest), 60_000); // min 1 minute

    // Build bars per agent
    const agentLanes = new Map<string, TimelineBar[]>();

    for (const session of allSessions) {
      const sessionStart = new Date(session.startedUtc).getTime();
      for (const agent of session.agents) {
        if (!agentLanes.has(agent.agentName)) {
          agentLanes.set(agent.agentName, []);
        }
        const laneBars = agentLanes.get(agent.agentName)!;

        for (const action of agent.actions) {
          const startStr = (action as any).startedUtc;
          const startMs = startStr ? new Date(startStr).getTime() : sessionStart;
          const durSec = (action as any).durationSeconds;
          const durMs = durSec && durSec > 0
            ? durSec * 1000
            : action.status === 'Running'
              ? now - startMs
              : 5000;

          const offsetPercent = ((startMs - earliest) / total) * 100;
          const widthPercent = Math.max(0.5, (durMs / total) * 100);

          // Determine stacking lane
          let lane = 0;
          for (const existing of laneBars) {
            const eEnd = existing.offsetPercent + existing.widthPercent;
            if (offsetPercent < eEnd && (offsetPercent + widthPercent) > existing.offsetPercent) {
              lane = Math.max(lane, existing.lane + 1);
            }
          }

          const bar: TimelineBar = {
            agentName: agent.agentName,
            actionTag: action.tag || action.command || 'Action',
            sessionTag: session.watchItemTag,
            status: action.status,
            offsetPercent,
            widthPercent,
            lane,
          };
          laneBars.push(bar);
          bars.push(bar);
        }
      }
    }

    const cursor = ((now - earliest) / total) * 100;

    return {
      timeOrigin: earliest,
      totalMs: total,
      agentBars: agentLanes,
      cursorPercent: Math.min(cursor, 100),
    };
  }, [allSessions, now]);

  // Time axis labels
  const timeLabels = useMemo(() => {
    const labels: { label: string; percent: number }[] = [];
    const intervalMs = 15_000; // 15s
    for (let t = 0; t <= totalMs; t += intervalMs) {
      const mins = Math.floor(t / 60_000);
      const secs = Math.floor((t % 60_000) / 1000);
      labels.push({
        label: `${mins}:${secs.toString().padStart(2, '0')}`,
        percent: (t / totalMs) * 100,
      });
    }
    return labels;
  }, [totalMs]);

  const agentNames = useMemo(
    () => Array.from(agentBars.keys()).sort(),
    [agentBars]
  );

  return (
    <div className="flex flex-col h-full overflow-hidden">
      <StatsBar />

      {/* Time axis */}
      <div className="relative h-6 ml-[120px] mr-2 bg-bg-surface border-b border-bdr">
        {timeLabels.map((t, i) => (
          <span
            key={i}
            className="absolute text-[9px] text-text-muted"
            style={{ left: `${t.percent}%`, top: 2 }}
          >
            {t.label}
          </span>
        ))}
        {/* Cursor */}
        <div
          className="absolute top-0 bottom-0 w-px bg-accent opacity-60"
          style={{ left: `${cursorPercent}%` }}
        />
      </div>

      {/* Gantt body */}
      <div className="flex-1 overflow-auto" ref={containerRef}>
        {agentNames.length === 0 && (
          <div className="text-center py-12 text-text-muted text-sm">
            No active sessions. Timeline will appear when actions are running.
          </div>
        )}

        {agentNames.map(agentName => {
          const bars = agentBars.get(agentName) || [];
          const maxLane = bars.reduce((m, b) => Math.max(m, b.lane), 0);
          const laneHeight = 32;
          const rowHeight = (maxLane + 1) * laneHeight + 8;

          return (
            <div key={agentName} className="flex border-b border-bdr" style={{ minHeight: rowHeight }}>
              {/* Agent label */}
              <div className="w-[120px] shrink-0 px-3 py-2 bg-bg-surface border-r border-bdr flex items-center">
                <span className="font-mono text-xs font-medium text-text-primary">
                  {agentName}
                </span>
              </div>

              {/* Bars */}
              <div className="flex-1 relative" style={{ minHeight: rowHeight }}>
                {bars.map((bar, i) => {
                  const style = statusColors[bar.status] || statusColors.Pending;
                  const label = bar.actionTag.length > 20
                    ? bar.actionTag.substring(0, 18) + '...'
                    : bar.actionTag;

                  return (
                    <div
                      key={i}
                      title={`${bar.actionTag}\nSession: ${bar.sessionTag}\nStatus: ${bar.status}`}
                      className={`absolute rounded ${style.bg} ${style.text} text-[10px] px-1.5 flex items-center overflow-hidden whitespace-nowrap cursor-default`}
                      style={{
                        left: `${bar.offsetPercent}%`,
                        width: `${bar.widthPercent}%`,
                        top: 4 + bar.lane * laneHeight,
                        height: laneHeight - 6,
                        minWidth: 4,
                      }}
                    >
                      {label}
                    </div>
                  );
                })}

                {/* Cursor line */}
                <div
                  className="absolute top-0 bottom-0 w-px bg-accent opacity-40"
                  style={{ left: `${cursorPercent}%` }}
                />
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}
