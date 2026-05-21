import { useState } from 'react';
import { Plus, Trash2, Pencil, Server, Wifi } from 'lucide-react';
import { useAgentStore } from '../../stores/agentStore';
import { useAgents } from '../../hooks/useAgents';
import { isAgentOnline } from '../../lib/agentStatus';

export default function RegistryPage() {
  const agents = useAgentStore(s => s.agents);
  const { registerAgent, unregisterAgent, testAgent } = useAgents();

  const [showForm, setShowForm] = useState(false);
  const [editingName, setEditingName] = useState<string | null>(null);
  const [formName, setFormName] = useState('');
  const [formAddress, setFormAddress] = useState('http://localhost:5200');
  const [regResult, setRegResult] = useState<{ healthy: boolean; message: string; detail: string | null } | null>(null);
  const [regLoading, setRegLoading] = useState(false);

  const handleSubmit = async () => {
    if (!formName.trim() || !formAddress.trim()) return;
    setRegLoading(true);
    setRegResult(null);
    try {
      const result = await registerAgent(formName.trim(), formAddress.trim());
      setRegResult({ healthy: result.healthy, message: result.message, detail: result.detail });
      if (result.healthy) {
        setTimeout(() => { resetForm(); }, 2000);
      }
    } catch (err: any) {
      setRegResult({ healthy: false, message: 'Registration request failed', detail: err?.response?.data ?? err?.message ?? 'Unknown error' });
    } finally {
      setRegLoading(false);
    }
  };

  const handleEdit = (name: string, address: string) => {
    setEditingName(name);
    setFormName(name);
    setFormAddress(address);
    setShowForm(true);
  };

  const handleUpdate = async () => {
    if (!editingName || !formAddress.trim()) return;
    setRegLoading(true);
    setRegResult(null);
    try {
      const result = await registerAgent(formName.trim() || editingName, formAddress.trim());
      setRegResult({ healthy: result.healthy, message: result.message, detail: result.detail });
      if (result.healthy) {
        setTimeout(() => { resetForm(); }, 2000);
      }
    } catch (err: any) {
      setRegResult({ healthy: false, message: 'Update request failed', detail: err?.response?.data ?? err?.message ?? 'Unknown error' });
    } finally {
      setRegLoading(false);
    }
  };

  const resetForm = () => {
    setShowForm(false);
    setEditingName(null);
    setFormName('');
    setFormAddress('http://localhost:5200');
    setRegResult(null);
    setRegLoading(false);
  };

  return (
    <div className="h-full flex flex-col">
      {/* Header */}
      <div className="flex items-center justify-between px-4 py-2 border-b border-bdr">
        <h2 className="text-sm font-semibold text-text-primary">Agent Registry</h2>
        <button
          className="flex items-center gap-1 px-2 py-1 rounded text-xs font-medium bg-acc-green/15 text-acc-green hover:bg-acc-green/25"
          onClick={() => { resetForm(); setShowForm(true); }}
        >
          <Plus size={12} /> Register Agent
        </button>
      </div>

      {/* Register/Edit Form */}
      {showForm && (
        <div className="px-4 py-3 border-b border-bdr bg-bg-card/50">
          <div className="flex items-end gap-2">
            <div className="flex-1">
              <label className="block text-[10px] text-text-muted uppercase mb-0.5">Name</label>
              <input
                className="w-full bg-white/5 border border-bdr rounded px-2 py-1.5 text-xs text-text-primary placeholder:text-text-muted outline-none focus:border-accent"
                placeholder="Agent name"
                value={formName}
                onChange={e => setFormName(e.target.value)}
                disabled={editingName !== null}
              />
            </div>
            <div className="flex-1">
              <label className="block text-[10px] text-text-muted uppercase mb-0.5">Address</label>
              <input
                className="w-full bg-white/5 border border-bdr rounded px-2 py-1.5 text-xs text-text-primary placeholder:text-text-muted outline-none focus:border-accent"
                placeholder="http://host:port"
                value={formAddress}
                onChange={e => setFormAddress(e.target.value)}
              />
            </div>
            <button
              className="px-3 py-1.5 rounded text-xs font-medium bg-accent/20 text-accent hover:bg-accent/30 disabled:opacity-40"
              onClick={editingName ? handleUpdate : handleSubmit}
              disabled={regLoading}
            >
              {regLoading ? 'Checking…' : editingName ? 'Update' : 'Register'}
            </button>
            <button
              className="px-2 py-1.5 rounded text-xs text-text-muted hover:bg-white/5"
              onClick={resetForm}
            >
              Cancel
            </button>
          </div>
          {regResult && (
            <div className={`mt-2 px-2 py-1.5 rounded text-xs ${
              regResult.healthy ? 'bg-acc-green/10 text-acc-green' : 'bg-acc-red/10 text-acc-red'
            }`}>
              <p className="font-medium">{regResult.message}</p>
              {regResult.detail && <p className="mt-0.5 text-[10px] opacity-80">{regResult.detail}</p>}
            </div>
          )}
        </div>
      )}

      {/* Agent Table */}
      <div className="flex-1 overflow-auto">
        {agents.length === 0 ? (
          <div className="flex items-center justify-center h-full text-text-muted text-xs">
            No agents registered. Click "Register Agent" to add one.
          </div>
        ) : (
          <table className="w-full text-xs">
            <thead className="sticky top-0 bg-bg-base">
              <tr className="border-b border-bdr text-text-muted text-left">
                <th className="px-4 py-2 font-medium">Agent</th>
                <th className="px-4 py-2 font-medium">Address</th>
                <th className="px-4 py-2 font-medium">Status</th>
                <th className="px-4 py-2 font-medium">Last Checked</th>
                <th className="px-4 py-2 font-medium w-28">Actions</th>
              </tr>
            </thead>
            <tbody>
              {agents.map(a => {
                const isOnline = isAgentOnline(a.status);
                return (
                  <tr key={a.name} className="border-b border-bdr/30 hover:bg-white/[0.02]">
                    <td className="px-4 py-2">
                      <div className="flex items-center gap-1.5">
                        <Server size={12} className="text-text-muted" />
                        <span className="text-text-primary font-medium">{a.name}</span>
                      </div>
                    </td>
                    <td className="px-4 py-2 text-text-secondary font-mono">{a.address}</td>
                    <td className="px-4 py-2">
                      <span className={`inline-flex items-center gap-1 px-1.5 py-0.5 rounded ${
                        isOnline ? 'bg-acc-green/15 text-acc-green' : 'bg-acc-red/15 text-acc-red'
                      }`}>
                        {a.status}
                      </span>
                    </td>
                    <td className="px-4 py-2 text-text-muted">
                      {a.lastCheckedUtc ? new Date(a.lastCheckedUtc).toLocaleTimeString() : '—'}
                    </td>
                    <td className="px-4 py-2">
                      <div className="flex items-center gap-1">
                        <button
                          className="p-1 rounded hover:bg-white/10 text-text-muted hover:text-accent"
                          title="Test connection"
                          onClick={() => testAgent(a.name)}
                        >
                          <Wifi size={12} />
                        </button>
                        <button
                          className="p-1 rounded hover:bg-white/10 text-text-muted hover:text-accent"
                          title="Edit"
                          onClick={() => handleEdit(a.name, a.address)}
                        >
                          <Pencil size={12} />
                        </button>
                        <button
                          className="p-1 rounded hover:bg-white/10 text-text-muted hover:text-acc-red"
                          title="Unregister"
                          onClick={() => unregisterAgent(a.name)}
                        >
                          <Trash2 size={12} />
                        </button>
                      </div>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}
