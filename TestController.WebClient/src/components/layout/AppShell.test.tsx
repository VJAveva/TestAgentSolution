import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';

const { apiFetch, stub } = vi.hoisted(() => ({
  apiFetch: vi.fn(),
  // AppShell composes every page; stub them so this test is only about the nav and routing.
  stub: (name: string) => ({ default: () => name }),
}));
vi.mock('../../lib/api', () => ({ apiFetch: (...a: unknown[]) => apiFetch(...a) }));

vi.mock('../watchlist/WatchListTree', () => stub('WatchListTree'));
vi.mock('../watchlist/NodeProperties', () => stub('NodeProperties'));
vi.mock('../watchlist/WatchListToolbar', () => stub('WatchListToolbar'));
vi.mock('../agents/AgentWorkspace', () => stub('AgentWorkspace'));
vi.mock('../execution/LiveLogger', () => stub('LiveLogger'));
vi.mock('../execution/SessionList', () => stub('SessionList'));
vi.mock('../execution/ExecutionDashboard', () => stub('ExecutionDashboard'));
vi.mock('../execution/TimelineView', () => stub('TimelineView'));
vi.mock('../execution/UnifiedLogView', () => stub('UnifiedLogView'));
vi.mock('../execution/LogViewer', () => stub('LogViewer'));
vi.mock('../results/BuildList', () => stub('BuildList'));
vi.mock('../results/BuildDetail', () => stub('BuildDetail'));
vi.mock('../results/TrendCharts', () => stub('TrendCharts'));
vi.mock('../reportcard/ReportCardView', () => stub('ReportCardView'));
vi.mock('../regression/RegressionView', () => stub('RegressionView'));
vi.mock('./Sidebar', () => ({ default: ({ children }: { children: unknown }) => children }));
vi.mock('./ConnectionStatus', () => stub('ConnectionStatus'));
vi.mock('../header/DefaultModeBanner', () => stub('DefaultModeBanner'));
vi.mock('../header/UserMenu', () => stub('UserMenu'));
vi.mock('../header/ThemeToggle', () => stub('ThemeToggle'));
vi.mock('../../hooks/useWatchList', () => ({ useWatchList: () => ({ fetchConfig: () => Promise.resolve() }) }));
vi.mock('../../hooks/useAgents', () => ({ useAgents: () => ({ fetchAgents: () => Promise.resolve() }) }));
vi.mock('../../hooks/useExecution', () => ({ useExecution: () => ({ fetchSessions: () => Promise.resolve() }) }));
vi.mock('../../hooks/useCapabilities', () => ({ useCan: () => true }));

import AppShell from './AppShell';
import { useFeatureStore } from '../../stores/featureStore';
import { useSystemModeStore } from '../../stores/systemModeStore';

function features(map: Record<string, boolean>) {
  return { teamName: 'T', features: [], map };
}

describe('AppShell feature toggles', () => {
  beforeEach(() => {
    apiFetch.mockReset();
    useFeatureStore.setState({ map: {}, loaded: false });
    useSystemModeStore.setState({ mode: 'secured', isSecured: true, isDefault: false });
    window.location.hash = '';
  });

  it('hides a disabled feature tab and keeps the others', async () => {
    apiFetch.mockResolvedValue(features({ CodeChurn: false, Results: true, Reports: true }));
    render(<AppShell />);

    await waitFor(() => expect(screen.queryByRole('button', { name: 'CodeChurn' })).toBeNull());
    expect(screen.getByRole('button', { name: 'Results' })).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Report Card' })).toBeTruthy();
  });

  it('shows every tab when /api/features is unavailable', async () => {
    apiFetch.mockRejectedValue(new Error('404'));
    render(<AppShell />);

    await waitFor(() => expect(useFeatureStore.getState().loaded).toBe(true));
    expect(screen.getByRole('button', { name: 'CodeChurn' })).toBeTruthy();
  });

  it('shows the turned-off message for a direct URL to a disabled feature', async () => {
    window.location.hash = '#/regression';
    apiFetch.mockResolvedValue(features({ CodeChurn: false }));
    render(<AppShell />);

    expect(await screen.findByText('This feature is turned off on this controller.')).toBeTruthy();
    expect(screen.queryByText('RegressionView')).toBeNull();
  });
});
