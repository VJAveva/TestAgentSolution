import { useSystemModeStore } from '../../stores/systemModeStore';

/**
 * Fixed (non-dismissible) Default Mode banner.
 * Per design: "Banner is fixed (not dismissible) by design —
 * it is the discoverability anchor for the upgrade path."
 *
 * Only renders when mode === 'default'.
 */
export default function DefaultModeBanner() {
  const isDefault = useSystemModeStore((s) => s.isDefault);

  if (!isDefault) return null;

  return (
    <div className="flex items-center gap-2 px-4 py-1.5 bg-yellow-500/10 border-b border-yellow-500/30 shrink-0">
      <span className="text-yellow-400 text-sm font-medium">⚠ Default Mode</span>
      <span className="text-xs text-text-secondary">
        No authentication required. Web Client is read-only (Observer). Contact your administrator to enable Secured mode.
      </span>
    </div>
  );
}
