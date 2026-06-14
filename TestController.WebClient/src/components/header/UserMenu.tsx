import { useState, useRef, useEffect } from 'react';
import { useAuthStore } from '../../stores/authStore';
import { useSystemModeStore } from '../../stores/systemModeStore';
import UserIdentityBadge from './UserIdentityBadge';
import ChangePasswordDialog from './ChangePasswordDialog';

/**
 * User menu: wraps UserIdentityBadge with a dropdown containing
 * "Change password" (non-guest, secured mode only) and "Sign out".
 */
export default function UserMenu() {
  const [open, setOpen] = useState(false);
  const [showChangePassword, setShowChangePassword] = useState(false);
  const menuRef = useRef<HTMLDivElement>(null);

  const isDefault = useSystemModeStore((s) => s.isDefault);
  const user = useAuthStore((s) => s.user);
  const logout = useAuthStore((s) => s.logout);

  // Close dropdown on outside click
  useEffect(() => {
    if (!open) return;
    const handler = (e: MouseEvent) => {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) {
        setOpen(false);
      }
    };
    document.addEventListener('mousedown', handler);
    return () => document.removeEventListener('mousedown', handler);
  }, [open]);

  // In Default mode, no dropdown — just show badge
  if (isDefault) {
    return <UserIdentityBadge />;
  }

  const isGuest = user?.isGuest ?? false;

  return (
    <>
      <div className="relative" ref={menuRef}>
        <button
          onClick={() => setOpen((v) => !v)}
          className="cursor-pointer"
          aria-haspopup="true"
          aria-expanded={open}
        >
          <UserIdentityBadge />
        </button>

        {open && (
          <div className="absolute right-0 top-full mt-1 w-44 rounded border border-border-default bg-bg-secondary shadow-lg z-50 py-1">
            {!isGuest && (
              <button
                onClick={() => { setOpen(false); setShowChangePassword(true); }}
                className="w-full text-left px-3 py-1.5 text-xs text-text-primary hover:bg-white/5 transition-colors"
              >
                Change password
              </button>
            )}
            <button
              onClick={() => { setOpen(false); logout(); }}
              className="w-full text-left px-3 py-1.5 text-xs text-text-primary hover:bg-white/5 transition-colors"
            >
              Sign out
            </button>
          </div>
        )}
      </div>

      {showChangePassword && (
        <ChangePasswordDialog onClose={() => setShowChangePassword(false)} />
      )}
    </>
  );
}
