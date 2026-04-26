import { useState, useEffect } from 'react';
import { X } from 'lucide-react';
import axios from 'axios';
import { apiFetch } from '../../lib/api';
import { getUserId } from '../../lib/userIdentity';

interface TriggerDialogProps {
  watchItemTag: string;
  isOpen: boolean;
  onClose: () => void;
  onTrigger: (buildNumber: string, dropLocation: string, lockVersion?: number) => void;
}

interface AvailableBuild {
  name: string;
  path: string;
  modified: string;
}

interface CanTriggerResult {
  canTrigger: boolean;
  lockVersion: number;
  watchItemTag: string;
  requiredAgents: string[];
  conflicts: {
    agentName: string;
    lockedBy: string;
    pipeline: string;
    duration: string;
    source: string;
  }[];
}

export default function TriggerDialog({ watchItemTag, isOpen, onClose, onTrigger }: TriggerDialogProps) {
  const [buildNumber, setBuildNumber] = useState('');
  const [dropLocation, setDropLocation] = useState('');
  const [availableBuilds, setAvailableBuilds] = useState<AvailableBuild[]>([]);
  const [currentParams, setCurrentParams] = useState<Record<string, string>>({});
  const [loading, setLoading] = useState(false);
  const [canTrigger, setCanTrigger] = useState<CanTriggerResult | null>(null);
  const [checkingAgents, setCheckingAgents] = useState(false);
  const [error, setError] = useState('');
  const [lockVersion, setLockVersion] = useState(0);

  const refreshCanTrigger = () => {
    apiFetch<CanTriggerResult>(`/api/execution/can-trigger/${encodeURIComponent(watchItemTag)}`)
      .then(data => {
        setCanTrigger(data);
        setLockVersion(data.lockVersion);
      })
      .catch(() => {});
  };

  // Subscribe to real-time lock changes while dialog is open
  useEffect(() => {
    if (!isOpen) return;
    const handler = () => refreshCanTrigger();
    window.addEventListener('agent-locks-changed', handler);
    return () => window.removeEventListener('agent-locks-changed', handler);
  }, [isOpen, watchItemTag]);

  useEffect(() => {
    if (!isOpen) return;
    setError('');
    setCanTrigger(null);

    // Pre-flight: check agent availability
    setCheckingAgents(true);
    apiFetch<CanTriggerResult>(`/api/execution/can-trigger/${encodeURIComponent(watchItemTag)}`)
      .then(data => {
        setCanTrigger(data);
        setLockVersion(data.lockVersion);
      })
      .catch(() => {})
      .finally(() => setCheckingAgents(false));

    // Load current parameters for this WatchItem
    axios.get(`/api/watchlist/${encodeURIComponent(watchItemTag)}/parameters`)
      .then(({ data }) => {
        setCurrentParams(data.parameters || {});
        setBuildNumber(data.parameters?.['_BuildNumber'] || '');
        setDropLocation(data.parameters?.['_DropLocation'] || '');

        const basePath = data.parameters?.['_BuildBasePath'] || '';
        if (basePath) {
          axios.get('/api/execution/available-builds', { params: { basePath } })
            .then(({ data: d }) => setAvailableBuilds(d.builds || []))
            .catch(() => {});
        }
      })
      .catch(() => {});
  }, [isOpen, watchItemTag]);

  const handleTrigger = () => {
    setLoading(true);
    setError('');
    onTrigger(buildNumber, dropLocation, lockVersion);
  };

  const selectBuild = (build: AvailableBuild) => {
    setBuildNumber(build.name);
    setDropLocation(build.path);
  };

  if (!isOpen) return null;

  const hasConflicts = canTrigger != null && !canTrigger.canTrigger;

  return (
    <div className="fixed inset-0 bg-black/60 flex items-center justify-center z-50">
      <div className="bg-bg-card border border-bdr rounded-lg w-[560px] max-h-[80vh] overflow-auto shadow-xl">
        {/* Header */}
        <div className="flex items-center justify-between px-5 py-3 border-b border-bdr">
          <div>
            <h2 className="text-sm font-bold text-text-primary">Trigger: {watchItemTag}</h2>
            <p className="text-[11px] text-text-muted mt-0.5">
              Provide build details or use current values from Variables.txt
            </p>
          </div>
          <button onClick={onClose} className="p-1 hover:bg-white/10 rounded text-text-muted hover:text-text-primary">
            <X size={16} />
          </button>
        </div>

        {/* Body */}
        <div className="px-5 py-4 space-y-4">
          {/* Agent availability check */}
          {checkingAgents && (
            <div className="text-text-muted text-xs animate-pulse">Checking agent availability…</div>
          )}
          {canTrigger && (
            <div className={`rounded-lg p-3 border ${
              hasConflicts
                ? 'bg-red-900/15 border-red-800/40'
                : 'bg-green-900/15 border-green-800/40'
            }`}>
              <div className="flex items-center gap-2 mb-1">
                <span className={`w-2 h-2 rounded-full ${hasConflicts ? 'bg-acc-red' : 'bg-acc-green'}`} />
                <span className={`text-xs font-semibold ${hasConflicts ? 'text-acc-red' : 'text-acc-green'}`}>
                  {hasConflicts ? 'Agents Not Available' : 'All Agents Free — Ready'}
                </span>
              </div>
              {canTrigger.requiredAgents.length > 0 && (
                <div className="text-[11px] text-text-muted mt-1">
                  Required: {canTrigger.requiredAgents.map((a, i) => (
                    <span key={a}>
                      {i > 0 && ', '}
                      <span className={`font-mono ${
                        canTrigger.conflicts.some(c => c.agentName === a) ? 'text-acc-red font-bold' : 'text-acc-green'
                      }`}>{a}</span>
                    </span>
                  ))}
                </div>
              )}
              {hasConflicts && canTrigger.conflicts.map(c => (
                <div key={c.agentName} className="flex items-center justify-between py-1 border-t border-red-800/20 text-[11px] mt-1">
                  <span><span className="font-mono text-acc-red font-bold">{c.agentName}</span> <span className="text-text-muted">locked by</span> <span className="text-amber-300">{c.lockedBy}</span></span>
                  <span className="text-text-muted">{c.pipeline} • <span className="font-mono">{c.duration}</span></span>
                </div>
              ))}
            </div>
          )}

          {/* Build Number */}
          <div>
            <label className="block text-[10px] font-semibold text-text-secondary uppercase tracking-wider mb-1">
              Build Number
            </label>
            <input
              type="text"
              value={buildNumber}
              onChange={e => setBuildNumber(e.target.value)}
              placeholder="e.g., OAK_main_20260406.5"
              className="w-full bg-bg-panel border border-bdr rounded px-3 py-1.5
                         text-text-primary font-mono text-xs focus:border-accent outline-none"
            />
          </div>

          {/* Drop Location */}
          <div>
            <label className="block text-[10px] font-semibold text-text-secondary uppercase tracking-wider mb-1">
              Drop Location (Full Path)
            </label>
            <input
              type="text"
              value={dropLocation}
              onChange={e => setDropLocation(e.target.value)}
              placeholder="\\\\server\\share\\build"
              className="w-full bg-bg-panel border border-bdr rounded px-3 py-1.5
                         text-text-primary font-mono text-xs focus:border-accent outline-none"
            />
          </div>

          {/* Available Builds */}
          {availableBuilds.length > 0 && (
            <div>
              <label className="block text-[10px] font-semibold text-text-secondary uppercase tracking-wider mb-1">
                Recent Builds (click to select)
              </label>
              <div className="max-h-36 overflow-auto bg-bg-panel border border-bdr rounded">
                {availableBuilds.map((b, i) => (
                  <button
                    key={i}
                    onClick={() => selectBuild(b)}
                    className={`w-full text-left px-3 py-1.5 text-xs font-mono
                      hover:bg-white/5 border-b border-bdr last:border-b-0 transition-colors
                      ${b.name === buildNumber ? 'bg-accent/15 text-accent' : 'text-text-primary'}`}
                  >
                    {b.name}
                    <span className="text-[10px] text-text-muted ml-2">
                      {new Date(b.modified).toLocaleDateString()}
                    </span>
                  </button>
                ))}
              </div>
            </div>
          )}

          {/* Current Parameters Preview */}
          {Object.keys(currentParams).length > 0 && (
            <details className="text-xs">
              <summary className="text-[10px] font-semibold text-text-secondary uppercase tracking-wider cursor-pointer">
                Current Variables.txt ({Object.keys(currentParams).length} parameters)
              </summary>
              <div className="mt-2 bg-bg-panel border border-bdr rounded p-3 font-mono text-[11px] text-text-muted max-h-28 overflow-auto">
                {Object.entries(currentParams).map(([k, v]) => (
                  <div key={k}>{k} = {v}</div>
                ))}
              </div>
            </details>
          )}

          {/* Error */}
          {error && (
            <div className="text-acc-red text-xs bg-red-900/20 rounded p-3">{error}</div>
          )}
        </div>

        {/* Footer */}
        <div className="px-5 py-3 border-t border-bdr flex justify-end gap-2">
          <button onClick={onClose}
            className="px-3 py-1.5 text-xs text-text-secondary hover:text-text-primary">
            Cancel
          </button>
          <button
            onClick={handleTrigger}
            disabled={loading || hasConflicts}
            className="px-4 py-1.5 bg-acc-green/20 text-acc-green hover:bg-acc-green/30
                       text-xs font-semibold rounded disabled:opacity-50 transition-colors"
          >
            {loading ? 'Triggering...' : hasConflicts ? 'Agents Busy' : 'Trigger Execution'}
          </button>
        </div>
      </div>
    </div>
  );
}
