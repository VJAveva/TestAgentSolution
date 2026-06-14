import { useState, useEffect } from 'react';
import { Eye, Server, Play, BarChart3, Activity, ScrollText } from 'lucide-react';
import WatchListTree from '../watchlist/WatchListTree';
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
import Sidebar from './Sidebar';
import ConnectionStatus from './ConnectionStatus';
import DefaultModeBanner from '../header/DefaultModeBanner';
import UserMenu from '../header/UserMenu';
import { useWatchList } from '../../hooks/useWatchList';
import { useAgents } from '../../hooks/useAgents';
import { useExecution } from '../../hooks/useExecution';
import { useSystemModeStore } from '../../stores/systemModeStore';

type Tab = 'watchlist' | 'agents' | 'execution' | 'monitor' | 'logs' | 'results';

const tabs: { id: Tab; label: string; icon: React.ReactNode }[] = [
  { id: 'watchlist', label: 'WatchList', icon: <Eye size={16} /> },
  { id: 'agents',    label: 'Agents',    icon: <Server size={16} /> },
  { id: 'execution', label: 'Execution', icon: <Play size={16} /> },
  { id: 'monitor',   label: 'Monitor',   icon: <Activity size={16} /> },
  { id: 'logs',      label: 'Logs',      icon: <ScrollText size={16} /> },
  { id: 'results',   label: 'Results',   icon: <BarChart3 size={16} /> },
];

export default function AppShell() {
  const [activeTab, setActiveTab] = useState<Tab>('watchlist');
  const { fetchConfig } = useWatchList();
  const { fetchAgents } = useAgents();
  const { fetchSessions } = useExecution();
  const fetchMode = useSystemModeStore((s) => s.fetchMode);

  useEffect(() => {
    fetchMode().catch(err => console.error('Failed to load system mode:', err));
    fetchConfig().catch(err => console.error('Failed to load watchlist:', err));
    fetchAgents().catch(err => console.error('Failed to load agents:', err));
    fetchSessions().catch(err => console.error('Failed to load execution sessions:', err));
  }, [fetchMode, fetchConfig, fetchAgents, fetchSessions]);

  return (
    <div className="flex flex-col h-screen bg-bg">
      {/* Top navigation */}
      <header className="flex items-center bg-bg-ribbon border-b border-bdr px-4 h-11 shrink-0">
        <span className="text-accent font-bold text-sm tracking-wide mr-8">TestController</span>
        <nav className="flex gap-1">
          {tabs.map(t => (
            <button
              key={t.id}
              onClick={() => setActiveTab(t.id)}
              className={`flex items-center gap-1.5 px-3 py-1.5 rounded text-xs font-medium transition-colors
                ${activeTab === t.id
                  ? 'bg-white/10 text-accent'
                  : 'text-text-secondary hover:bg-white/5 hover:text-text-primary'}`}
            >
              {t.icon}{t.label}
            </button>
          ))}
        </nav>
        <div className="ml-auto flex items-center gap-3">
          <UserMenu />
          <ConnectionStatus />
        </div>
      </header>

      {/* Default Mode Banner — fixed, non-dismissible */}
      <DefaultModeBanner />

      {/* Tab content */}
      <div className="flex flex-1 overflow-hidden">
        {activeTab === 'watchlist' && <WatchListPage />}
        {activeTab === 'agents'    && <AgentsPage />}
        {activeTab === 'execution' && <ExecutionPage />}
        {activeTab === 'monitor'   && <MonitorPage />}
        {activeTab === 'logs'      && <LogsPage />}
        {activeTab === 'results'   && <ResultsPage />}
      </div>
    </div>
  );
}

function WatchListPage() {
  return (
    <>
      <Sidebar title="WatchList">
        <WatchListToolbar />
        <WatchListTree />
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

function MonitorPage() {
  const [subTab, setSubTab] = useState<'pipeline' | 'timeline' | 'log'>('pipeline');

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
            onClick={() => setSubTab(t.id)}
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
