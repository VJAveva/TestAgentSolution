import { useAuthStore } from '../stores/authStore';
import { useSystemModeStore } from '../stores/systemModeStore';
import { can, getDisabledReason } from '../lib/capabilities';

/**
 * Returns whether the current user can perform the given permission.
 * Binds capabilities.ts to authStore + systemModeStore state.
 */
export function useCan(permission: string, resourceId?: string): boolean {
  const user = useAuthStore((s) => s.user);
  const mode = useSystemModeStore((s) => s.mode);
  return can(user, mode, permission, resourceId);
}

/**
 * Returns a tooltip reason string if the permission is denied, or null if allowed.
 */
export function useDisabledReason(permission: string, resourceId?: string): string | null {
  const user = useAuthStore((s) => s.user);
  const mode = useSystemModeStore((s) => s.mode);
  return getDisabledReason(user, mode, permission, resourceId);
}
