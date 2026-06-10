import { useSystemModeStore } from '../../stores/systemModeStore';

type RoleVariant = 'Administrator' | 'SeniorManager' | 'Engineer' | 'Guest' | 'Observer' | 'Default';

interface UserIdentityBadgeProps {
  role?: RoleVariant;
  displayName?: string;
}

const roleConfig: Record<RoleVariant, { icon: string; label: string; color: string; bg: string }> = {
  Administrator: { icon: '🛡', label: 'Admin', color: 'border-red-400', bg: 'bg-red-400/10' },
  SeniorManager: { icon: '👔', label: 'Sr. Mgr', color: 'border-orange-400', bg: 'bg-orange-400/10' },
  Engineer: { icon: '🔧', label: 'Engineer', color: 'border-blue-400', bg: 'bg-blue-400/10' },
  Guest: { icon: '🎫', label: 'Guest', color: 'border-green-400', bg: 'bg-green-400/10' },
  Observer: { icon: '👁', label: 'Observer', color: 'border-gray-400', bg: 'bg-gray-400/10' },
  Default: { icon: '👤', label: 'Default', color: 'border-gray-500 border-dashed', bg: 'bg-transparent' },
};

/**
 * User identity badge in the header chrome.
 * Handles all role variants: Admin, SrMgr, Engineer, Guest, Observer, Default user.
 * In Default mode, renders as Observer (Web Client) with dashed border.
 */
export default function UserIdentityBadge({ role, displayName }: UserIdentityBadgeProps) {
  const isDefault = useSystemModeStore((s) => s.isDefault);

  // In Default mode, Web Client always shows as Observer
  const effectiveRole: RoleVariant = isDefault ? 'Observer' : (role ?? 'Default');
  const effectiveName = isDefault ? 'Observer' : (displayName ?? 'User');

  const config = roleConfig[effectiveRole] ?? roleConfig.Default;

  return (
    <div className={`flex items-center gap-1.5 px-2 py-1 rounded border ${config.color} ${config.bg}`}>
      <span className="text-xs">{config.icon}</span>
      <span className="text-xs text-text-primary font-medium">{effectiveName}</span>
      <span className="text-[10px] text-text-secondary opacity-70">{config.label}</span>
    </div>
  );
}
