import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import ForceReleaseDialog from './ForceReleaseDialog';
import type { PipelineLockDto } from '../../stores/lockStore';

vi.mock('../../lib/api', () => ({
  apiFetch: vi.fn(),
}));

const mockLock: PipelineLockDto = {
  pipelineId: 'WarmSetup-Four-Nodes',
  ownerUserId: 'user-abc-123',
  ownerDisplayName: 'ravi.kumar',
  ownerClientKind: 'Web',
  acquiredUtc: '2026-06-12T10:00:00Z',
  expiresUtc: '2026-06-12T10:30:00Z',
};

describe('ForceReleaseDialog', () => {
  const onClose = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('Should_RenderNothing_When_NotOpen', () => {
    const { container } = render(<ForceReleaseDialog open={false} lock={mockLock} onClose={onClose} />);
    expect(container.innerHTML).toBe('');
  });

  it('Should_DisableSubmit_When_ReasonTooShort', () => {
    render(<ForceReleaseDialog open={true} lock={mockLock} onClose={onClose} />);
    const submitBtns = screen.getAllByRole('button').filter(b => b.textContent?.includes('Take over and revert'));
    expect(submitBtns[0]).toBeDisabled();
  });

  it('Should_DisableSubmit_When_CheckboxUnchecked', () => {
    render(<ForceReleaseDialog open={true} lock={mockLock} onClose={onClose} />);
    const textarea = screen.getByPlaceholderText(/Explain why/);
    fireEvent.change(textarea, { target: { value: 'This is a valid reason for taking over' } });
    // Checkbox not checked
    const submitBtns = screen.getAllByRole('button').filter(b => b.textContent?.includes('Take over and revert'));
    expect(submitBtns[0]).toBeDisabled();
  });

  it('Should_EnableSubmit_When_BothConditionsMet', () => {
    render(<ForceReleaseDialog open={true} lock={mockLock} onClose={onClose} />);
    const textarea = screen.getByPlaceholderText(/Explain why/);
    fireEvent.change(textarea, { target: { value: 'This is a valid reason for taking over' } });
    const checkbox = screen.getByRole('checkbox');
    fireEvent.click(checkbox);
    const submitBtns = screen.getAllByRole('button').filter(b => b.textContent?.includes('Take over and revert'));
    expect(submitBtns[0]).not.toBeDisabled();
  });

  it('Should_ShowCharacterCounter_When_Typing', () => {
    render(<ForceReleaseDialog open={true} lock={mockLock} onClose={onClose} />);
    expect(screen.getByText('0 / 10 minimum')).toBeTruthy();
    const textarea = screen.getByPlaceholderText(/Explain why/);
    fireEvent.change(textarea, { target: { value: 'short' } });
    expect(screen.getByText('5 / 10 minimum')).toBeTruthy();
  });

  it('Should_ShowGreenCounter_When_ThresholdMet', () => {
    render(<ForceReleaseDialog open={true} lock={mockLock} onClose={onClose} />);
    const textarea = screen.getByPlaceholderText(/Explain why/);
    fireEvent.change(textarea, { target: { value: '1234567890' } });
    const counter = screen.getByText('10 / 10 minimum');
    expect(counter.className).toContain('text-green-400');
  });

  it('Should_DisplayOwnerName_When_InAffirmationLabel', () => {
    render(<ForceReleaseDialog open={true} lock={mockLock} onClose={onClose} />);
    expect(screen.getByText(/ravi\.kumar's run will be cancelled/)).toBeTruthy();
  });
});
