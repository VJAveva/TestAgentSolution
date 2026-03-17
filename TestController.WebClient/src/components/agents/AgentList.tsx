import { useState } from 'react';
import { Server, Plus, Trash2, Wifi, WifiOff } from 'lucide-react';
import { useAgentStore } from '../../stores/agentStore';
import { useAgents } from '../../hooks/useAgents';

export default function AgentList() {
  const agents = useAgentStore(s => s.agents);
  const selectedAgent = useAgentStore(s => s.selectedAgent);
  const selectAgent = useAgentStore(s => s.selectAgent);
  const { registerAgent, unregisterAgent, testAgent } = useAgents();

  const [showRegister, setShowRegister] = useState(false);
  const [name, setName] = useState('');
  const [address, setAddress] = useState('http://localhost:5200');

  const handleRegister = async () => {
    if (!name.trim()) return;
    await registerAgent(name.trim(), address.trim());
    setName(''); setAddress('http://localhost:5200'); setShowRegister(false);
  };

  return (
    <div className="flex flex-col">
      <div className="flex items-center gap-1 p-2 border-b border-bdr">
        <button
          className="flex items-center gap-1 px-2 py-1 rounded text-xs font-medium bg-acc-green/15 text-acc-green hover:bg-acc-green/25"
          onClick={() => setShowRegister(v => !v)}
        >
          <Plus size={12} /> Register
        </button>
      </div>

      {showRegister && (
        <div className="p-2 border-b border-bdr space-y-1.5 bg-bg-card/50">
          <input
            className="w-full bg-white/5 border border-bdr rounded px-2 py-1 text-xs text-text-primary placeholder:text-text-muted outline-none focus:border-accent"
            placeholder="Agent name" value={name} onChange={e => setName(e.target.value)}
          />
          <input
            className="w-full bg-white/5 border border-bdr rounded px-2 py-1 text-xs text-text-primary placeholder:text-text-muted outline-none focus:border-accent"
            placeholder="Address" value={address} onChange={e => setAddress(e.target.value)}
          />
          <button className="w-full bg-accent/20 text-accent text-xs py-1 rounded hover:bg-accent/30" onClick={handleRegister}>
            Add Agent
          </button>
        </div>
      )}

      <div className="flex-1 overflow-auto">
        {agents.length === 0 && <p className="p-3 text-xs text-text-muted">No agents registered.</p>}
        {agents.map(a => {
          const isOnline = ['Connected', 'Online', 'Healthy', 'AgentStateReady'].some(
            s => a.status.toLowerCase().includes(s.toLowerCase()));
          const isSelected = selectedAgent === a.name;
          return (
            <div
              key={a.name}
              className={`flex items-center gap-2 px-3 py-2 cursor-pointer border-b border-bdr/50 text-xs transition-colors
                ${isSelected ? 'bg-accent/15 text-accent' : 'hover:bg-white/5 text-text-primary'}`}
              onClick={() => selectAgent(a.name)}
            >
              <Server size={14} className="shrink-0 text-text-muted" />
              <div className="flex-1 min-w-0">
                <div className="font-medium truncate">{a.name}</div>
                <div className="text-text-muted truncate">{a.address}</div>
              </div>
              {isOnline
                ? <Wifi size={12} className="text-acc-green shrink-0" />
                : <WifiOff size={12} className="text-acc-red shrink-0" />}
              <span className={`text-[10px] px-1.5 py-0.5 rounded ${isOnline ? 'bg-acc-green/20 text-acc-green' : 'bg-acc-red/20 text-acc-red'}`}>
                {a.status}
              </span>
              <button
                className="p-1 hover:bg-white/10 rounded text-text-muted hover:text-acc-red"
                title="Test"
                onClick={e => { e.stopPropagation(); testAgent(a.name); }}
              >
                <Wifi size={12} />
              </button>
              <button
                className="p-1 hover:bg-white/10 rounded text-text-muted hover:text-acc-red"
                title="Remove"
                onClick={e => { e.stopPropagation(); unregisterAgent(a.name); }}
              >
                <Trash2 size={12} />
              </button>
            </div>
          );
        })}
      </div>
    </div>
  );
}
