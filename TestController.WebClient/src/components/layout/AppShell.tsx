import { useState, useEffect, useCallback } from 'react';
import { Eye, Server, Play, BarChart3, Activity, ScrollText, AlertTriangle, RotateCw, Award, GitPullRequest } from 'lucide-react';import WatchListTree from '../watchlist/WatchListTree';
import NodeProperties from '../watchlist/NodeProperties';
import WatchListToolbar from '../watchlist/WatchListToolbar';
import AgentWorkspace from '../agents/AgentWorkspace';
import LiveLogger from '../execution/LiveLogger';
import SessionList from '../execution/SessionList';
import ExecutionDashboard from '../execution/ExecutionDashboard';
import TimelineView from '../execution/TimelineView';
import UnifiedLogView from '../execution/UnifiedLogView';
import LogViewer from '../execution/LogViewer';
import BuildList from '../results/BuildList';
import BuildDetail from '../results/BuildDetail';
import TrendCharts from '../results/TrendCharts';
import ReportCardView from '../reportcard/ReportCardView';
import RegressionView from '../regression/RegressionView';
import Sidebar from './Sidebar';
import ConnectionStatus from './ConnectionStatus';
import DefaultModeBanner from '../header/DefaultModeBanner';
import UserMenu from '../header/UserMenu';
import ThemeToggle from '../header/ThemeToggle';
import { useWatchList } from '../../hooks/useWatchList';
import { useAgents } from '../../hooks/useAgents';
import { useExecution } from '../../hooks/useExecution';
import { useHashRoute } from '../../hooks/useHashRoute';
import { useCan } from '../../hooks/useCapabilities';
import { useSystemModeStore } from '../../stores/systemModeStore';
import { mark, measure } from '../../lib/uiPerf';

type Tab = 'watchlist' | 'agents' | 'execution' | 'monitor' | 'logs' | 'results' | 'report' | 'regression';

const tabs: { id: Tab; label: string; icon: React.ReactNode }[] = [
  { id: 'watchlist', label: 'WatchList', icon: <Eye size={16} /> },
  { id: 'agents',    label: 'Agents',    icon: <Server size={16} /> },
  { id: 'execution', label: 'Execution', icon: <Play size={16} /> },
  { id: 'monitor',   label: 'Monitor',   icon: <Activity size={16} /> },
  { id: 'logs',      label: 'Logs',      icon: <ScrollText size={16} /> },
  { id: 'results',   label: 'Results',   icon: <BarChart3 size={16} /> },
  { id: 'report',    label: 'Report Card', icon: <Award size={16} /> },
  { id: 'regression', label: 'CodeChurn', icon: <GitPullRequest size={16} /> },
];

export default function AppShell() {
  const [route, navigate] = useHashRoute();
  const [seg0, seg1] = route.split('/');
  const activeTab = (tabs.find(t => t.id === seg0)?.id ?? 'watchlist') as Tab;
  const [loadError, setLoadError] = useState<string | null>(null);
  const { fetchConfig } = useWatchList();
  const { fetchAgents } = useAgents();
  const { fetchSessions } = useExecution();

  // These features are intentionally Secured-mode only. Default mode is the legacy/no-RBAC mode and must not
  // present Code Churn or Report Card as usable screens backed by mock or incomplete data.
  const isSecured = useSystemModeStore((s) => s.isSecured);
  const canReportCard = useCan('ReportCard_View');
  const canCodeChurn = useCan('CodeChurn_View');
  const denied: Partial<Record<Tab, boolean>> = {
    report: !isSecured || !canReportCard,
    regression: !isSecured || !canCodeChurn,
  };

  const loadAll = useCallback(() => {
    setLoadError(null);
    Promise.allSettled([fetchConfig(), fetchAgents(), fetchSessions()]).then(results => {
      const failed = results.filter(r => r.status === 'rejected');
      if (failed.length) {
        setLoadError(`Failed to load ${failed.length} resource(s). Check controller connectivity.`);
      }
    });
  }, [fetchConfig, fetchAgents, fetchSessions]);

  useEffect(() => {
    loadAll();
  }, [loadAll]);

  // TEMPORARY (P03): time from route switch to first paint of that route.
  useEffect(() => {
    mark(`route:${activeTab}`);
    const id = requestAnimationFrame(() => measure(`route-load:${activeTab}`, `route:${activeTab}`));
    return () => cancelAnimationFrame(id);
  }, [activeTab]);

  return (
    <div className="flex flex-col h-screen bg-bg">
      {/* Top navigation */}
      <header className="flex items-center bg-bg-ribbon border-b border-bdr px-4 h-11 shrink-0">
        <span className="text-accent font-bold text-sm tracking-wide mr-8">TestController</span>
        <nav className="flex gap-1">
          {tabs.map(t => {
            const isDenied = denied[t.id] === true;
            return (
              <button
                key={t.id}
                onClick={() => !isDenied && navigate(t.id)}
                disabled={isDenied}
                title={isDenied ? 'Requires Administrator or Sr Manager' : undefined}
                className={`flex items-center gap-1.5 px-3 py-1.5 rounded text-xs font-medium transition-colors
                  ${isDenied
                    ? 'text-text-secondary/40 cursor-not-allowed'
                    : activeTab === t.id
                      ? 'bg-white/10 text-accent'
                      : 'text-text-secondary hover:bg-white/5 hover:text-text-primary'}`}
              >
                {t.icon}{t.label}
              </button>
            );
          })}
        </nav>
        <div className="ml-auto flex items-center gap-3">
          <ThemeToggle />
          <UserMenu />
          <ConnectionStatus />
        </div>
      </header>

      {/* Default Mode Banner — fixed, non-dismissible */}
      <DefaultModeBanner />

      {/* Initial load error — actionable, with retry */}
      {loadError && (
        <div className="flex items-center gap-3 bg-acc-red/10 border-b border-acc-red/30 px-4 py-2 text-xs text-acc-red shrink-0">
          <AlertTriangle size={14} className="shrink-0" />
          <span className="flex-1">{loadError}</span>
          <button
            onClick={loadAll}
            className="flex items-center gap-1.5 px-2.5 py-1 rounded bg-acc-red/15 hover:bg-acc-red/25 text-acc-red font-medium transition-colors"
          >
            <RotateCw size={12} /> Retry
          </button>
        </div>
      )}

      {/* Tab content */}
      <div className="flex flex-1 overflow-hidden">
        {activeTab === 'watchlist' && <WatchListPage />}
        {activeTab === 'agents'    && <AgentsPage />}
        {activeTab === 'execution' && <ExecutionPage />}
        {activeTab === 'monitor'   && <MonitorPage subTab={seg1} navigate={navigate} />}
        {activeTab === 'logs'      && <LogsPage />}
        {activeTab === 'results'   && <ResultsPage />}
        {activeTab === 'report'    && (denied.report ? <PermissionDenied /> : <ReportCardPage />)}
        {activeTab === 'regression' && (denied.regression ? <PermissionDenied /> : <RegressionPage />)}
      </div>
    </div>
  );
}

// Reached by typing a hash route directly; the nav button for it is already disabled.
function PermissionDenied() {
  return (
    <div className="flex flex-1 items-center justify-center text-xs text-text-secondary">
      <div className="flex items-center gap-2">
        <AlertTriangle size={14} className="text-acc-red" />
        Available only in Secured mode for authorized users.
      </div>
    </div>
  );
}

function WatchListPage() {
  return (
    <>
      <Sidebar title="WatchList">
        <div className="flex flex-col h-full">
          <WatchListToolbar />
          <div className="flex-1 min-h-0">
            <WatchListTree />
          </div>
        </div>
      </Sidebar>
      <main className="flex-1 overflow-auto p-4">
        <NodeProperties />
      </main>
    </>
  );
}

function AgentsPage() {
  return (
    <main className="flex-1 overflow-hidden">
      <AgentWorkspace />
    </main>
  );
}

function ExecutionPage() {
  return (
    <>
      <Sidebar title="Sessions">
        <SessionList />
      </Sidebar>
      <main className="flex-1 overflow-auto p-4">
        <LiveLogger />
      </main>
    </>
  );
}

function ResultsPage() {
  return (
    <>
      <Sidebar title="Builds">
        <BuildList />
      </Sidebar>
      <main className="flex-1 overflow-auto p-4 space-y-4">
        <BuildDetail />
        <TrendCharts />
      </main>
    </>
  );
}

function ReportCardPage() {
  return (
    <main className="flex-1 flex overflow-hidden">
      <ReportCardView />
    </main>
  );
}

function RegressionPage() {
  return (
    <main className="flex-1 flex overflow-hidden">
      <RegressionView />
    </main>
  );
}

function MonitorPage({ subTab: seg1, navigate }: { subTab: string | undefined; navigate: (route: string) => void }) {
  const subTab = (['pipeline', 'timeline', 'log'].includes(seg1 ?? '') ? seg1 : 'pipeline') as 'pipeline' | 'timeline' | 'log';

  return (
    <main className="flex-1 flex flex-col overflow-hidden">
      {/* Sub-tab bar */}
      <div className="flex items-center gap-1 px-4 py-1.5 border-b border-bdr shrink-0">
        {([
          { id: 'pipeline' as const, label: 'Pipeline' },
          { id: 'timeline' as const, label: 'Timeline' },
          { id: 'log' as const, label: 'Unified Log' },
        ]).map(t => (
          <button
            key={t.id}
            onClick={() => navigate(`monitor/${t.id}`)}
            className={`text-xs px-3 py-1 rounded transition-colors ${
              subTab === t.id
                ? 'bg-white/10 text-accent font-medium'
                : 'text-text-secondary hover:bg-white/5 hover:text-text-primary'
            }`}
          >
            {t.label}
          </button>
        ))}
      </div>
      <div className="flex-1 overflow-hidden">
        {subTab === 'pipeline' && <ExecutionDashboard />}
        {subTab === 'timeline' && <TimelineView />}
        {subTab === 'log' && <UnifiedLogView />}
      </div>
    </main>
  );
}

function LogsPage() {
  return (
    <main className="flex-1 overflow-hidden">
      <LogViewer />
    </main>
  );
}
