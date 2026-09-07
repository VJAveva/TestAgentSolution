import { describe, it, expect } from 'vitest';
import { can, getDisabledReason, PERMISSION_CATALOG } from './capabilities';
import type { AuthUser } from '../stores/authStore';
import type { SystemMode } from '../stores/systemModeStore';

function makeUser(role: string, assignedPipelineIds: string[] = []): AuthUser {
  return {
    userId: 'user-1',
    username: 'testuser',
    displayName: 'Test User',
    role,
    clientKind: 'Web',
    capabilities: PERMISSION_CATALOG[role]?.slice() ?? [],
    mustChangePassword: false,
    isGuest: role === 'Guest',
    assignedPipelineIds,
  };
}

describe('PERMISSION_CATALOG', () => {
  it('Administrator has 22 permissions', () => {
    expect(PERMISSION_CATALOG.Administrator).toHaveLength(22);
  });
  it('SeniorManager has 13 permissions', () => {
    expect(PERMISSION_CATALOG.SeniorManager).toHaveLength(13);
  });
  it('Engineer has 6 permissions', () => {
    expect(PERMISSION_CATALOG.Engineer).toHaveLength(6);
  });
  it('Guest has 2 permissions', () => {
    expect(PERMISSION_CATALOG.Guest).toHaveLength(2);
  });
});

describe('can()', () => {
  // Table-driven matrix mirroring server AuthorizationServiceTests
  const cases: [string, SystemMode, string, string | undefined, boolean][] = [
    // Default mode — web is read-only
    ['Administrator', 'default', 'Pipeline_View', undefined, true],
    ['Administrator', 'default', 'Pipeline_Trigger', 'pipe-A', false],
    ['Administrator', 'default', 'Report_View', undefined, true],
    ['Engineer', 'default', 'Pipeline_Trigger', 'pipe-A', false],
    ['Guest', 'default', 'Pipeline_View', undefined, true],
    ['Guest', 'default', 'Pipeline_Trigger', undefined, false],

    // Secured mode — Administrator all-yes
    ['Administrator', 'secured', 'Pipeline_Trigger', 'any-pipe', true],
    ['Administrator', 'secured', 'User_Create', undefined, true],
    ['Administrator', 'secured', 'Audit_Export', undefined, true],

    // Secured mode — SeniorManager
    ['SeniorManager', 'secured', 'Pipeline_Trigger', 'any-pipe', true],
    ['SeniorManager', 'secured', 'Pipeline_Cancel', 'any-pipe', true],
    ['SeniorManager', 'secured', 'Pipeline_TriggerAll', undefined, true],
    ['SeniorManager', 'secured', 'User_Create', undefined, false],
    ['SeniorManager', 'secured', 'Audit_View', undefined, false],

    // Secured mode — Engineer + assigned
    ['Engineer', 'secured', 'Pipeline_Trigger', 'pipe-A', true],
    ['Engineer', 'secured', 'Pipeline_Cancel', 'pipe-A', true],
    ['Engineer', 'secured', 'Pipeline_Retry', 'pipe-A', true],
    ['Engineer', 'secured', 'Pipeline_View', undefined, true],
    ['Engineer', 'secured', 'Report_View', undefined, true],

    // Secured mode — Engineer + NOT assigned
    ['Engineer', 'secured', 'Pipeline_Trigger', 'pipe-X', false],
    ['Engineer', 'secured', 'Pipeline_Cancel', 'pipe-X', false],

    // Secured mode — Engineer lacks permission
    ['Engineer', 'secured', 'User_Create', undefined, false],
    ['Engineer', 'secured', 'Pipeline_TriggerAll', undefined, false],
    ['Engineer', 'secured', 'Pipeline_ForceRelease', undefined, false],

    // Secured mode — Guest
    ['Guest', 'secured', 'Pipeline_View', undefined, true],
    ['Guest', 'secured', 'Report_View', undefined, true],
    ['Guest', 'secured', 'Pipeline_Trigger', 'any-pipe', false],
    ['Guest', 'secured', 'Pipeline_Cancel', undefined, false],
    ['Guest', 'secured', 'User_Create', undefined, false],
  ];

  it.each(cases)(
    'can(%s, %s, %s, %s) → %s',
    (role, mode, permission, resourceId, expected) => {
      const user = makeUser(role, ['pipe-A', 'pipe-B']);
      expect(can(user, mode, permission, resourceId)).toBe(expected);
    },
  );

  it('returns false when user is null in secured mode', () => {
    expect(can(null, 'secured', 'Pipeline_View')).toBe(false);
  });

  it('returns true for Pipeline_View in default mode even with null user', () => {
    expect(can(null, 'default', 'Pipeline_View')).toBe(true);
  });
});

describe('getDisabledReason()', () => {
  it('returns null when permission is allowed', () => {
    const admin = makeUser('Administrator');
    expect(getDisabledReason(admin, 'secured', 'Pipeline_Trigger', 'pipe-A')).toBeNull();
  });

  it('returns default mode message for write permission in default mode', () => {
    const admin = makeUser('Administrator');
    const reason = getDisabledReason(admin, 'default', 'Pipeline_Trigger', 'pipe-A');
    expect(reason).toContain('Default mode');
  });

  it('returns role message for Guest trying to trigger', () => {
    const guest = makeUser('Guest');
    const reason = getDisabledReason(guest, 'secured', 'Pipeline_Trigger', 'pipe-A');
    expect(reason).toContain('Guest');
    expect(reason).toContain('does not have trigger permissions');
  });

  it('returns assignment message for Engineer on unassigned pipeline', () => {
    const engineer = makeUser('Engineer', ['pipe-A']);
    const reason = getDisabledReason(engineer, 'secured', 'Pipeline_Trigger', 'pipe-X');
    expect(reason).toContain('not assigned');
  });

  it('returns not authenticated message for null user', () => {
    const reason = getDisabledReason(null, 'secured', 'Pipeline_Trigger');
    expect(reason).toContain('not authenticated');
  });
});
