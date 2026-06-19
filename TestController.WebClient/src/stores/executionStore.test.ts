import { describe, it, expect, beforeEach } from 'vitest';
import { useExecutionStore } from './executionStore';
import type { LogEntry } from '../types/api';

function makeEntries(count: number, prefix = 'L'): LogEntry[] {
  return Array.from({ length: count }, (_, i) => ({
    message: `${prefix}${i}`,
    timestamp: '',
    severity: 'info' as const,
  }));
}

describe('executionStore', () => {
  beforeEach(() => {
    useExecutionStore.setState({ logs: [], isLogPaused: false, maxLogs: 2000 });
  });

  // ?? addLogs (A1 batch append) ??????????????????????????????????????

  it('Should_AppendAll_When_AddLogsBatch', () => {
    useExecutionStore.getState().addLogs(makeEntries(50));
    expect(useExecutionStore.getState().logs).toHaveLength(50);
  });

  it('Should_TrimToMax_When_BatchExceedsCap', () => {
    useExecutionStore.setState({ maxLogs: 10 });
    useExecutionStore.getState().addLogs(makeEntries(25));
    const logs = useExecutionStore.getState().logs;
    expect(logs).toHaveLength(10);
    // Trim keeps the most recent entries (oldest spliced off the front).
    expect(logs[0].message).toBe('L15');
    expect(logs[9].message).toBe('L24');
  });

  it('Should_TrimAcrossCalls_When_RunningTotalExceedsCap', () => {
    useExecutionStore.setState({ maxLogs: 10 });
    useExecutionStore.getState().addLogs(makeEntries(8, 'A'));
    useExecutionStore.getState().addLogs(makeEntries(8, 'B'));
    const logs = useExecutionStore.getState().logs;
    expect(logs).toHaveLength(10);
    expect(logs[logs.length - 1].message).toBe('B7');
  });

  it('Should_NotAppend_When_PausedAndAddLogs', () => {
    useExecutionStore.setState({ isLogPaused: true });
    useExecutionStore.getState().addLogs(makeEntries(5));
    expect(useExecutionStore.getState().logs).toHaveLength(0);
  });

  it('Should_NoOp_When_AddLogsEmptyBatch', () => {
    const before = useExecutionStore.getState();
    useExecutionStore.getState().addLogs([]);
    expect(useExecutionStore.getState()).toBe(before);
    expect(useExecutionStore.getState().logs).toHaveLength(0);
  });

  // ?? addLog (single) ?????????????????????????????????????????????????

  it('Should_AppendOne_When_AddLog', () => {
    useExecutionStore.getState().addLog({ message: 'one', timestamp: '', severity: 'info' });
    expect(useExecutionStore.getState().logs).toHaveLength(1);
  });

  it('Should_NotAppend_When_PausedAndAddLog', () => {
    useExecutionStore.setState({ isLogPaused: true });
    useExecutionStore.getState().addLog({ message: 'x', timestamp: '', severity: 'info' });
    expect(useExecutionStore.getState().logs).toHaveLength(0);
  });

  // ?? clearLogs / togglePause ?????????????????????????????????????????

  it('Should_EmptyLogs_When_ClearLogs', () => {
    useExecutionStore.getState().addLogs(makeEntries(3));
    useExecutionStore.getState().clearLogs();
    expect(useExecutionStore.getState().logs).toHaveLength(0);
  });

  it('Should_FlipPause_When_TogglePause', () => {
    expect(useExecutionStore.getState().isLogPaused).toBe(false);
    useExecutionStore.getState().togglePause();
    expect(useExecutionStore.getState().isLogPaused).toBe(true);
    useExecutionStore.getState().togglePause();
    expect(useExecutionStore.getState().isLogPaused).toBe(false);
  });
});
