import { useState, useEffect } from 'react';
import { LayoutGrid, Activity, Database } from 'lucide-react';
import { useAgentStore } from '../../stores/agentStore';
import { useAgents } from '../../hooks/useAgents';
import FleetPage from './FleetPage';
import MonitorPage from './MonitorPage';
import RegistryPage from './RegistryPage';
import type { WorkspaceMode } from '../../types/agentWorkspace';

/**
 * AgentWorkspace - the main workspace component for the "agents" tab.
 * Provides three sub-modes: Fleet (grid overview), Monitor (telemetry detail), Registry (table + CRUD).
 * Replaces the old AgentList + AgentDetail layout.
 */
export default function AgentWorkspace() {
  const [mode, setMode] = useState<WorkspaceMode>('fleet');
  const [monitorAgent, setMonitorAgent] = useState<string | null>(null);
  const { fetchAgents } = useAgents();

  // Keep agent store populated for Registry mode
  useEffect(() => { fetchAgents(); }, [fetchAgents]);

  const handleSelectAgent = (name: string) => {
    setMonitorAgent(name);
    setMode('monitor');
  };

  const handleBackToFleet = () => {
    setMonitorAgent(null);
    setMode('fleet');
  };

  return (
    <div className="flex flex-col h-full">
      {/* Mode Selector Bar */}
      <div className="flex items-center gap-1 px-3 py-1.5 border-b border-bdr bg-bg-base">
        <ModeTab mode="fleet" label="Fleet" icon={<LayoutGrid size={13} />} current={mode} onClick={setMode} />
        <ModeTab mode="monitor" label="Monitor" icon={<Activity size={13} />} current={mode} onClick={setMode} disabled={!monitorAgent} />
        <ModeTab mode="registry" label="Registry" icon={<Database size={13} />} current={mode} onClick={setMode} />
      </div>

      {/* Content */}
      <div className="flex-1 min-h-0">
        {mode === 'fleet' && <FleetPage onSelectAgent={handleSelectAgent} />}
        {mode === 'monitor' && monitorAgent && <MonitorPage agentName={monitorAgent} onBack={handleBackToFleet} />}
        {mode === 'registry' && <RegistryPage />}
      </div>
    </div>
  );
}

function ModeTab({ mode, label, icon, current, onClick, disabled }: {
  mode: WorkspaceMode; label: string; icon: React.ReactNode; current: WorkspaceMode; onClick: (m: WorkspaceMode) => void; disabled?: boolean;
}) {
  const isActive = current === mode;
  return (
    <button
      className={`flex items-center gap-1 px-2.5 py-1 rounded text-xs font-medium transition-colors ${
        isActive
          ? 'bg-accent/15 text-accent'
          : disabled
            ? 'text-text-muted/40 cursor-not-allowed'
            : 'text-text-muted hover:bg-white/5 hover:text-text-secondary'
      }`}
      onClick={() => !disabled && onClick(mode)}
      disabled={disabled}
    >
      {icon} {label}
    </button>
  );
}
