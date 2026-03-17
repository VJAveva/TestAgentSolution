import { useState, useEffect } from 'react';
import { Activity, Stethoscope, History, Shield } from 'lucide-react';
import { useAgentStore } from '../../stores/agentStore';
import { useAgents } from '../../hooks/useAgents';
import type { DiagnosticStep } from '../../types/api';

export default function AgentDetail() {
  const selectedAgent = useAgentStore(s => s.selectedAgent);
  const agents = useAgentStore(s => s.agents);
  const agent = agents.find(a => a.name === selectedAgent);

  if (!agent) {
    return <div className="flex items-center justify-center h-full text-text-muted text-sm">Select an agent to view details.</div>;
  }

  return (
    <div className="space-y-4">
      <h2 className="text-base font-semibold text-text-primary">{agent.name}</h2>
      <p className="text-xs text-text-muted">{agent.address}</p>
      <div className="grid grid-cols-2 gap-3">
        <SnapshotCard name={agent.name} />
        <DiagnoseCard name={agent.name} />
        <HealthCard name={agent.name} />
        <HistoryCard name={agent.name} />
      </div>
    </div>
  );
}

function SectionCard({ title, icon, children }: { title: string; icon: React.ReactNode; children: React.ReactNode }) {
  return (
    <div className="bg-bg-card rounded-lg p-3">
      <div className="flex items-center gap-1.5 mb-2 text-xs font-semibold text-text-secondary uppercase">
        {icon}{title}
      </div>
      {children}
    </div>
  );
}

function SnapshotCard({ name }: { name: string }) {
  const { getSnapshot } = useAgents();
  const [data, setData] = useState<Record<string, unknown> | null>(null);
  useEffect(() => { getSnapshot(name).then(setData).catch(() => setData(null)); }, [name, getSnapshot]);

  return (
    <SectionCard title="Snapshot" icon={<Activity size={12} />}>
      {data ? (
        <div className="space-y-1 text-xs">
          {Object.entries(data).filter(([, v]) => v !== null && v !== '').map(([k, v]) => (
            <div key={k} className="flex gap-2">
              <span className="text-text-muted w-36 shrink-0 capitalize">{k.replace(/([A-Z])/g, ' $1').trim()}</span>
              <span className="text-text-primary break-all">{typeof v === 'object' ? JSON.stringify(v) : String(v)}</span>
            </div>
          ))}
        </div>
      ) : <p className="text-xs text-text-muted">Loading…</p>}
    </SectionCard>
  );
}

function DiagnoseCard({ name }: { name: string }) {
  const { diagnoseAgent } = useAgents();
  const [steps, setSteps] = useState<DiagnosticStep[] | null>(null);
  const [loading, setLoading] = useState(false);

  const run = async () => {
    setLoading(true);
    try {
      const result = await diagnoseAgent(name);
      setSteps(result.steps);
    } catch { setSteps(null); }
    finally { setLoading(false); }
  };

  return (
    <SectionCard title="Diagnose" icon={<Stethoscope size={12} />}>
      <button
        className="mb-2 px-2 py-1 rounded text-xs bg-acc-mauve/15 text-acc-mauve hover:bg-acc-mauve/25 disabled:opacity-40"
        onClick={run} disabled={loading}
      >
        {loading ? 'Running…' : 'Run Diagnostics'}
      </button>
      {steps && (
        <div className="space-y-1">
          {steps.map((s, i) => (
            <div key={i} className="flex items-start gap-1.5 text-xs">
              <span className={s.passed ? 'text-acc-green' : 'text-acc-red'}>{s.passed ? '?' : '?'}</span>
              <span className="text-text-primary font-medium">{s.step}</span>
              <span className="text-text-muted ml-auto truncate max-w-[200px]">{s.detail}</span>
            </div>
          ))}
        </div>
      )}
    </SectionCard>
  );
}

function HealthCard({ name }: { name: string }) {
  const { getHealth } = useAgents();
  const [data, setData] = useState<Record<string, unknown> | null>(null);
  useEffect(() => { getHealth(name).then(setData).catch(() => setData(null)); }, [name, getHealth]);

  return (
    <SectionCard title="Health" icon={<Shield size={12} />}>
      {data ? (
        <div className="space-y-1 text-xs">
          {Object.entries(data).filter(([, v]) => v !== null && v !== '').map(([k, v]) => (
            <div key={k} className="flex gap-2">
              <span className="text-text-muted w-36 shrink-0 capitalize">{k.replace(/([A-Z])/g, ' $1').trim()}</span>
              <span className="text-text-primary">{String(v)}</span>
            </div>
          ))}
        </div>
      ) : <p className="text-xs text-text-muted">Loading…</p>}
    </SectionCard>
  );
}

function HistoryCard({ name }: { name: string }) {
  const { getHistory } = useAgents();
  const [records, setRecords] = useState<Record<string, unknown>[] | null>(null);
  useEffect(() => { getHistory(name, 5).then(setRecords).catch(() => setRecords(null)); }, [name, getHistory]);

  return (
    <SectionCard title="Recent History" icon={<History size={12} />}>
      {records && records.length > 0 ? (
        <div className="space-y-1.5 text-xs">
          {records.map((r, i) => (
            <div key={i} className="bg-bg-panel rounded p-1.5">
              <div className="font-medium text-text-primary truncate">{String(r.command || '')}</div>
              <div className="text-text-muted">Exit: {String(r.exitCode ?? '?')} · {String(r.outcome ?? '')}</div>
            </div>
          ))}
        </div>
      ) : <p className="text-xs text-text-muted">{records === null ? 'Loading…' : 'No history.'}</p>}
    </SectionCard>
  );
}
