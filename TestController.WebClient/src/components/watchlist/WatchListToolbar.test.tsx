import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';

vi.mock('../../hooks/useWatchList', () => ({
  useWatchList: () => ({ refresh: vi.fn(), importXml: vi.fn(), exportXml: vi.fn() }),
}));
vi.mock('../../hooks/useExecution', () => ({
  useExecution: () => ({ triggerAll: vi.fn(), triggerByTag: vi.fn(), cancelAll: vi.fn(), retryByTag: vi.fn() }),
}));

import WatchListToolbar from './WatchListToolbar';
import { useAuthStore, type AuthUser } from '../../stores/authStore';
import { useSystemModeStore } from '../../stores/systemModeStore';

function user(role: string): AuthUser {
  return {
    userId: 'u1', username: 'u', displayName: 'U', role, clientKind: 'Web', capabilities: [],
    mustChangePassword: false, isGuest: false, assignedPipelineIds: [],
  };
}

function importButton() {
  return screen.getByRole('button', { name: /Import/ }) as HTMLButtonElement;
}

describe('WatchListToolbar import guard', () => {
  beforeEach(() => {
    useAuthStore.setState({ user: null });
  });

  it('disables Import with a reason in Default mode', () => {
    useSystemModeStore.setState({ mode: 'default', isSecured: false, isDefault: true });
    render(<WatchListToolbar />);

    expect(importButton().disabled).toBe(true);
    expect(importButton().title).toContain('Administrator in Secured mode');
  });

  it('disables Import for an Engineer in Secured mode', () => {
    useSystemModeStore.setState({ mode: 'secured', isSecured: true, isDefault: false });
    useAuthStore.setState({ user: user('Engineer') });
    render(<WatchListToolbar />);

    expect(importButton().disabled).toBe(true);
  });

  it('enables Import for an Administrator in Secured mode', () => {
    useSystemModeStore.setState({ mode: 'secured', isSecured: true, isDefault: false });
    useAuthStore.setState({ user: user('Administrator') });
    render(<WatchListToolbar />);

    expect(importButton().disabled).toBe(false);
  });
});
