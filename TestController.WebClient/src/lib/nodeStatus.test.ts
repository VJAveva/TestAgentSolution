import { describe, it, expect } from 'vitest';
import { toNodeStatus, isTerminalNodeStatus } from './nodeStatus';

describe('toNodeStatus', () => {
  it.each([
    ['Running', 'Running'],
    ['Success', 'Success'],
    ['Failed', 'Failed'],
    ['Skipped', 'Skipped'],
    ['Cancelled', 'Cancelled'],
  ])('Should_Map_%s_To_%s', (input, expected) => {
    expect(toNodeStatus(input)).toBe(expected);
  });

  // The last status a node reports is what the tree shows forever, so an unknown value must not
  // leave it pulsing ('Running') or invisible ('Idle').
  it.each([undefined, '', 'Weird', 'PartialFailure'])(
    'Should_FailClosed_When_StatusIsUnrecognised_%s',
    (input) => {
      const result = toNodeStatus(input as string | undefined);
      expect(result).toBe('Failed');
      expect(isTerminalNodeStatus(result)).toBe(true);
    },
  );

  it('Should_TreatPendingAsIdle_When_ActionHasNotStarted', () => {
    expect(toNodeStatus('Pending')).toBe('Idle');
  });
});

describe('isTerminalNodeStatus', () => {
  it('Should_ReportTerminal_When_RunHasFinished', () => {
    expect(isTerminalNodeStatus('Success')).toBe(true);
    expect(isTerminalNodeStatus('Failed')).toBe(true);
    expect(isTerminalNodeStatus('Skipped')).toBe(true);
    expect(isTerminalNodeStatus('Cancelled')).toBe(true);
  });

  it('Should_ReportNonTerminal_When_StillInFlight', () => {
    expect(isTerminalNodeStatus('Running')).toBe(false);
    expect(isTerminalNodeStatus('Idle')).toBe(false);
  });
});
