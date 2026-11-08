import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';

const { apiFetch } = vi.hoisted(() => ({ apiFetch: vi.fn() }));
vi.mock('../../lib/api', () => ({ apiFetch: (...a: unknown[]) => apiFetch(...a) }));

import FeatureSettingsPanel from './FeatureSettingsPanel';
import { useFeatureStore } from '../../stores/featureStore';

const alpha = {
  name: 'TeamX.Alpha', url: null, branches: ['main'], includePaths: ['src/'], excludePaths: [], credentialRef: 'ADO_PAT',
};

function callTo(path: string) {
  return apiFetch.mock.calls.find(([url]) => url === path);
}

describe('FeatureSettingsPanel Code Churn repositories', () => {
  beforeEach(() => {
    apiFetch.mockReset();
    apiFetch.mockResolvedValue({ saved: true, message: 'Saved at 10:00:00.', changes: [] });
    useFeatureStore.setState({
      teamName: 'WAS-QA',
      features: [],
      map: {},
      codeChurn: { configured: true, repositories: [alpha] },
      fetchFeatures: () => Promise.resolve(),
    });
  });

  it('shows each configured repository with its branches', () => {
    render(<FeatureSettingsPanel />);

    expect((screen.getByLabelText('Name for repository 1') as HTMLInputElement).value).toBe('TeamX.Alpha');
    expect((screen.getByLabelText('Branches for repository 1') as HTMLInputElement).value).toBe('main');
  });

  it('saves an added repository with its own branches to the code-churn endpoint', async () => {
    render(<FeatureSettingsPanel />);

    fireEvent.click(screen.getByRole('button', { name: 'Add repository' }));
    fireEvent.change(screen.getByLabelText('Name for repository 2'), { target: { value: 'TeamX.Beta' } });
    fireEvent.change(screen.getByLabelText('Branches for repository 2'), { target: { value: 'develop, release/2026' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(callTo('/api/features/code-churn')).toBeTruthy());
    const body = JSON.parse(callTo('/api/features/code-churn')![1].body as string);
    expect(body.repositories.map((r: { name: string }) => r.name)).toEqual(['TeamX.Alpha', 'TeamX.Beta']);
    expect(body.repositories[1].branches).toEqual(['develop', 'release/2026']);
    expect(await screen.findByRole('status')).toBeTruthy();
  });

  it('does not call the code-churn endpoint when repositories are unchanged', async () => {
    render(<FeatureSettingsPanel />);

    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(callTo('/api/features')).toBeTruthy());
    expect(callTo('/api/features/code-churn')).toBeUndefined();
    expect((await screen.findByRole('status')).textContent).toBe('No changes to save.');
  });

  it('shows the reason when the repository save is refused', async () => {
    apiFetch.mockImplementation((url: string) =>
      url === '/api/features/code-churn'
        ? Promise.reject(new Error('CredentialRef looks like a token.'))
        : Promise.resolve({ saved: true, message: 'ok', changes: [] }));
    render(<FeatureSettingsPanel />);

    fireEvent.click(screen.getByLabelText('Remove repository 1'));
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));

    expect((await screen.findByRole('status')).textContent).toContain('CredentialRef looks like a token.');
  });
});
