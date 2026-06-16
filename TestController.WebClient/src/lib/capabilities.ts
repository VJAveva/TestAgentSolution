import type { AuthUser } from '../stores/authStore';
import type { SystemMode } from '../stores/systemModeStore';

/**
 * Client-side permission catalog mirroring PermissionCatalog.cs.
 * Source: TestControllerGrpc.Core/Authorization/PermissionCatalog.cs
 *
 * UX HINT ONLY — NOT security. Server-side guard is authoritative.
 */
export const PERMISSION_CATALOG: Readonly<Record<string, readonly string[]>> = Object.freeze({
  Administrator: Object.freeze([
    'Pipeline_View', 'Pipeline_Trigger', 'Pipeline_Cancel', 'Pipeline_Retry',
    'Pipeline_TriggerAll', 'Pipeline_CancelAll', 'Pipeline_Enable', 'Pipeline_Disable',
    'Pipeline_ForceRelease', 'User_Create', 'User_Update', 'User_Delete',
    'User_Assign', 'User_Revoke', 'Report_View', 'Report_Generate',
    'Audit_View', 'Audit_Export', 'Notification_Mute',
  ]),
  SeniorManager: Object.freeze([
    'Pipeline_View', 'Pipeline_Trigger', 'Pipeline_Cancel', 'Pipeline_Retry',
    'Pipeline_TriggerAll', 'Pipeline_CancelAll', 'Pipeline_ForceRelease',
    'Report_View', 'Report_Generate', 'Notification_Mute',
  ]),
  Engineer: Object.freeze([
    'Pipeline_View', 'Pipeline_Trigger', 'Pipeline_Cancel', 'Pipeline_Retry',
    'Report_View', 'Notification_Mute',
  ]),
  Guest: Object.freeze([
    'Pipeline_View', 'Report_View',
  ]),
});

/** Read-only permissions allowed for web users in Default mode. */
const DEFAULT_MODE_READ_PERMISSIONS: readonly string[] = Object.freeze([
  'Pipeline_View', 'Report_View',
]);

/**
 * Pure capability check replicating server AuthorizationService rules.
 * UX hint only — the server guard from Phase 2a is the source of truth.
 */
export function can(
  user: AuthUser | null,
  mode: SystemMode,
  permission: string,
  resourceId?: string,
): boolean {
  // Default mode: web is read-only (default-mode-web-readonly)
  if (mode === 'default') {
    return DEFAULT_MODE_READ_PERMISSIONS.includes(permission);
  }

  // No user in Secured mode = no permissions
  if (!user) return false;

  // Admin shortcut: all permissions
  if (user.role === 'Administrator') return true;

  // Role-based permission check
  const rolePermissions = PERMISSION_CATALOG[user.role];
  if (!rolePermissions || !rolePermissions.includes(permission)) return false;

  // Resource-scoped check for Engineers (assignment-based)
  if (resourceId && user.role === 'Engineer') {
    return (user.assignedPipelineIds ?? []).includes(resourceId);
  }

  return true;
}

/**
 * Returns a human-readable reason why a permission is denied, or null if allowed.
 * Tooltip text per Mockup 4 and Mockup 11.
 */
export function getDisabledReason(
  user: AuthUser | null,
  mode: SystemMode,
  permission: string,
  resourceId?: string,
): string | null {
  if (can(user, mode, permission, resourceId)) return null;

  // Default mode: web is read-only
  if (mode === 'default') {
    return 'Trigger is unavailable in Default mode. Switch to Secured mode to enable pipeline triggering from the Web Client.';
  }

  // No user
  if (!user) return 'You are not authenticated.';

  // Role lacks the permission entirely
  const rolePermissions = PERMISSION_CATALOG[user.role];
  if (!rolePermissions || !rolePermissions.includes(permission)) {
    return `Your role (${user.role}) does not have trigger permissions.`;
  }

  // Engineer not assigned to this pipeline
  if (resourceId && user.role === 'Engineer') {
    return 'Not assigned to you \u2014 contact an administrator to request access.';
  }

  return 'Permission denied.';
}
