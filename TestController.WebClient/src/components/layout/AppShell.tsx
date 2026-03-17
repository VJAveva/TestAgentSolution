import { useState, useEffect } from 'react';
import { Eye, Server, Play, BarChart3 } from 'lucide-react';
import WatchListTree from '../watchlist/WatchListTree';
import NodeProperties from '../watchlist/NodeProperties';
import WatchListToolbar from '../watchlist/WatchListToolbar';
import AgentList from '../agents/AgentList';
import AgentDetail from '../agents/AgentDetail';
import LiveLogger from '../execution/LiveLogger';
import SessionList from '../execution/SessionList';
import BuildList from '../results/BuildList';
import BuildDetail from '../results/BuildDetail';
import TrendCharts from '../results/TrendCharts';
import Sidebar from './Sidebar';
import { useWatchList } from '../../hooks/useWatchList';
import { useAgents } from '../../hooks/useAgents';
import { useExecution } from '../../hooks/useExecution';

type Tab = 'watchlist' | 'agents' | 'execution' | 'results';

const tabs: { id: Tab; label: string; icon: React.ReactNode }[] = [
  { id: 'watchlist', label: 'WatchList', icon: <Eye size={16} /> },
  { id: 'agents',    label: 'Agents',    icon: <Server size={16} /> },
  { id: 'execution', label: 'Execution', icon: <Play size={16} /> },
  { id: 'results',   label: 'Results',   icon: <BarChart3 size={16} /> },
];

export default function AppShell() {
  const [activeTab, setActiveTab] = useState<Tab>('watchlist');
  const { fetchConfig } = useWatchList();
  const { fetchAgents } = useAgents();
  const { fetchStatus } = useExecution();

  useEffect(() => {
    fetchConfig().catch(() => {});
    fetchAgents().catch(() => {});
    fetchStatus().catch(() => {});
  }, [fetchConfig, fetchAgents, fetchStatus]);

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
      </header>

      {/* Tab content */}
      <div className="flex flex-1 overflow-hidden">
        {activeTab === 'watchlist' && <WatchListPage />}
        {activeTab === 'agents'    && <AgentsPage />}
        {activeTab === 'execution' && <ExecutionPage />}
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
    <>
      <Sidebar title="Agents">
        <AgentList />
      </Sidebar>
      <main className="flex-1 overflow-auto p-4">
        <AgentDetail />
      </main>
    </>
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
