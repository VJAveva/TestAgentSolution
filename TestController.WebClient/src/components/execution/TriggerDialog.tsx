import { useState, useEffect } from 'react';
import { X } from 'lucide-react';
import axios from 'axios';

interface TriggerDialogProps {
  watchItemTag: string;
  isOpen: boolean;
  onClose: () => void;
  onTrigger: (buildNumber: string, dropLocation: string) => void;
}

interface AvailableBuild {
  name: string;
  path: string;
  modified: string;
}

export default function TriggerDialog({ watchItemTag, isOpen, onClose, onTrigger }: TriggerDialogProps) {
  const [buildNumber, setBuildNumber] = useState('');
  const [dropLocation, setDropLocation] = useState('');
  const [availableBuilds, setAvailableBuilds] = useState<AvailableBuild[]>([]);
  const [currentParams, setCurrentParams] = useState<Record<string, string>>({});
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    if (!isOpen) return;
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
    onTrigger(buildNumber, dropLocation);
  };

  const selectBuild = (build: AvailableBuild) => {
    setBuildNumber(build.name);
    setDropLocation(build.path);
  };

  if (!isOpen) return null;

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
        </div>

        {/* Footer */}
        <div className="px-5 py-3 border-t border-bdr flex justify-end gap-2">
          <button onClick={onClose}
            className="px-3 py-1.5 text-xs text-text-secondary hover:text-text-primary">
            Cancel
          </button>
          <button onClick={handleTrigger} disabled={loading}
            className="px-4 py-1.5 bg-acc-green/20 text-acc-green hover:bg-acc-green/30
                       text-xs font-semibold rounded disabled:opacity-50 transition-colors">
            {loading ? 'Triggering...' : 'Trigger Execution'}
          </button>
        </div>
      </div>
    </div>
  );
}
