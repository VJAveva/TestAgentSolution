import { useState, useEffect } from 'react';
import { Cpu, HardDrive, MemoryStick, Activity, ArrowLeft, History, Shield } from 'lucide-react';
import { useAgentTelemetry } from '../../hooks/useAgentTelemetry';
import { useAgents } from '../../hooks/useAgents';
import type { AgentTelemetry } from '../../types/agentWorkspace';

interface MonitorPageProps {
  agentName: string;
  onBack: () => void;
}

export default function MonitorPage({ agentName, onBack }: MonitorPageProps) {
  const { telemetry, error } = useAgentTelemetry(agentName);

  return (
    <div className="h-full flex flex-col overflow-hidden">
      {/* Header */}
      <div className="flex items-center gap-2 px-4 py-2 border-b border-bdr">
        <button
          className="p-1 rounded hover:bg-white/10 text-text-muted"
          onClick={onBack}
          title="Back to Fleet"
        >
          <ArrowLeft size={16} />
        </button>
        <h2 className="text-sm font-semibold text-text-primary">{agentName}</h2>
        {telemetry && (
          <span className={`ml-2 text-[10px] px-1.5 py-0.5 rounded ${
            telemetry.state === 'AgentStateReady' ? 'bg-acc-green/20 text-acc-green' : 'bg-acc-amber/20 text-acc-amber'
          }`}>
            {telemetry.state}
          </span>
        )}
        {error && <span className="ml-2 text-xs text-acc-red">{error}</span>}
      </div>

      {/* Metrics Cards */}
      <div className="grid grid-cols-2 xl:grid-cols-4 gap-3 p-4">
        <MetricCard
          label="CPU"
          value={telemetry?.cpuUsagePct ?? 0}
          unit="%"
          icon={<Cpu size={14} />}
          color={getMetricColor(telemetry?.cpuUsagePct ?? 0)}
        />
        <MetricCard
          label="Memory"
          value={telemetry?.memoryUsedMb ?? 0}
          unit={`/ ${telemetry?.memoryTotalMb ?? 0} MB`}
          icon={<MemoryStick size={14} />}
          color={getMetricColor(telemetry?.memoryTotalMb ? (telemetry.memoryUsedMb / telemetry.memoryTotalMb) * 100 : 0)}
        />
        <MetricCard
          label="Disk Free"
          value={telemetry?.diskFreeGb ?? 0}
          unit="GB"
          icon={<HardDrive size={14} />}
          color="text-acc-blue"
        />
        <MetricCard
          label="Processes"
          value={telemetry?.activeProcessCount ?? 0}
          unit=""
          icon={<Activity size={14} />}
          color="text-acc-mauve"
        />
      </div>

      {/* Activity Summary */}
      {telemetry && (
        <div className="px-4 pb-3">
          <div className="bg-bg-card rounded-lg p-3 space-y-1 text-xs">
            <div className="flex justify-between">
              <span className="text-text-muted">Activity</span>
              <span className="text-text-primary">{telemetry.currentActivity || 'Idle'}</span>
            </div>
            {telemetry.currentCommand && (
              <div className="flex justify-between">
                <span className="text-text-muted">Command</span>
                <span className="text-text-secondary font-mono truncate max-w-[280px]">{telemetry.currentCommand}</span>
              </div>
            )}
            <div className="flex justify-between">
              <span className="text-text-muted">Executions</span>
              <span className="text-text-primary">
                <span className="text-acc-green">{telemetry.executionsCompleted}</span>
                {' / '}
                <span className="text-acc-red">{telemetry.executionsFailed} failed</span>
              </span>
            </div>
          </div>
        </div>
      )}

      {/* Tabbed Content Area */}
      <MonitorTabs agentName={agentName} />
    </div>
  );
}

function MetricCard({ label, value, unit, icon, color }: {
  label: string; value: number; unit: string; icon: React.ReactNode; color: string;
}) {
  return (
    <div className="bg-bg-card rounded-lg p-3">
      <div className="flex items-center gap-1.5 text-text-muted text-[10px] uppercase font-semibold mb-1">
        {icon} {label}
      </div>
      <div className={`text-lg font-bold ${color}`}>
        {typeof value === 'number' ? value.toFixed(1) : value}
        <span className="text-xs font-normal text-text-muted ml-1">{unit}</span>
      </div>
    </div>
  );
}

function getMetricColor(percent: number): string {
  if (percent > 90) return 'text-acc-red';
  if (percent > 70) return 'text-acc-amber';
  return 'text-acc-green';
}

type TabId = 'history' | 'audit';

function MonitorTabs({ agentName }: { agentName: string }) {
  const [activeTab, setActiveTab] = useState<TabId>('history');

  return (
    <div className="flex-1 flex flex-col min-h-0 border-t border-bdr">
      <div className="flex border-b border-bdr">
        <TabButton id="history" label="History" icon={<History size={12} />} active={activeTab === 'history'} onClick={() => setActiveTab('history')} />
        <TabButton id="audit" label="Audit Log" icon={<Shield size={12} />} active={activeTab === 'audit'} onClick={() => setActiveTab('audit')} />
      </div>
      <div className="flex-1 overflow-auto p-3">
        {activeTab === 'history' && <HistoryPanel agentName={agentName} />}
        {activeTab === 'audit' && <AuditPanel agentName={agentName} />}
      </div>
    </div>
  );
}

function TabButton({ label, icon, active, onClick }: { id: string; label: string; icon: React.ReactNode; active: boolean; onClick: () => void }) {
  return (
    <button
      className={`flex items-center gap-1 px-3 py-2 text-xs font-medium border-b-2 transition-colors ${
        active ? 'border-accent text-accent' : 'border-transparent text-text-muted hover:text-text-secondary'
      }`}
      onClick={onClick}
    >
      {icon} {label}
    </button>
  );
}

function HistoryPanel({ agentName }: { agentName: string }) {
  const { getHistory } = useAgents();
  const [records, setRecords] = useState<any[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    setLoading(true);
    getHistory(agentName, 20).then(setRecords).catch(() => setRecords([])).finally(() => setLoading(false));
  }, [agentName, getHistory]);

  if (loading) return <p className="text-xs text-text-muted">Loading…</p>;
  if (records.length === 0) return <p className="text-xs text-text-muted">No execution history.</p>;

  return (
    <div className="space-y-1">
      {records.map((r: any, i: number) => (
        <div key={i} className="flex items-center gap-2 text-xs py-1 border-b border-bdr/30">
          <span className={`w-5 text-center ${r.exitCode === 0 ? 'text-acc-green' : 'text-acc-red'}`}>
            {r.exitCode === 0 ? '✓' : '✗'}
          </span>
          <span className="text-text-primary font-mono truncate flex-1">{r.command}</span>
          <span className="text-text-muted">{r.arguments}</span>
          <span className="text-text-muted shrink-0">{r.exitCode != null ? `exit ${r.exitCode}` : ''}</span>
        </div>
      ))}
    </div>
  );
}

function AuditPanel({ agentName }: { agentName: string }) {
  const { getAudit } = useAgents();
  const [entries, setEntries] = useState<any[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    setLoading(true);
    getAudit(agentName, 100).then(setEntries).catch(() => setEntries([])).finally(() => setLoading(false));
  }, [agentName, getAudit]);

  if (loading) return <p className="text-xs text-text-muted">Loading…</p>;
  if (entries.length === 0) return <p className="text-xs text-text-muted">No audit entries.</p>;

  return (
    <div className="space-y-1">
      {entries.map((e: any, i: number) => (
        <div key={i} className="flex items-start gap-2 text-xs py-1 border-b border-bdr/30">
          <span className={`shrink-0 px-1 rounded ${
            e.severity === 'Error' ? 'bg-acc-red/20 text-acc-red' :
            e.severity === 'Warning' ? 'bg-acc-amber/20 text-acc-amber' :
            'bg-white/5 text-text-muted'
          }`}>{e.severity?.[0] ?? 'I'}</span>
          <span className="text-text-muted shrink-0 w-16">{e.timestamp?.split('T')[1]?.slice(0, 8) ?? ''}</span>
          <span className="text-text-primary flex-1 truncate">{e.event}</span>
          {e.command && <span className="text-text-muted font-mono truncate max-w-[120px]">{e.command}</span>}
        </div>
      ))}
    </div>
  );
}
