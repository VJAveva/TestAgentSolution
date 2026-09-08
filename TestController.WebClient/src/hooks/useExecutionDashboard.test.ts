import { describe, it, expect } from 'vitest';
import { reducer } from './useExecutionDashboard';

/**
 * Guards the batching fix from docs/reliability/UIPerformance-CopilotPrompts.md.
 * The server batches agent output every ~500ms; the client must consume that batch
 * in ONE reducer pass. Fanning it back out to one dispatch per line produced one
 * React render per output line and undid the server-side batching.
 */
const emptyState = {
  sessions: new Map(),
  logs: [] as any[],
  selectedSessionId: null,
  selectedAgentName: null,
  maxLogs: 10000,
};

const outputs = (n: number) =>
  Array.from({ length: n }, (_, i) => ({
    agentName: 'jvgr1',
    line: `line ${i}`,
    kind: 'stdout',
    sessionId: 's1',
  }));

describe('useExecutionDashboard reducer', () => {
  it('appends a whole AgentOutputBatch in one pass', () => {
    const next = reducer(emptyState as any, { type: 'AGENT_OUTPUT_BATCH', batch: outputs(250) });
    expect(next.logs).toHaveLength(250);
    expect(next.logs[0].message).toBe('line 0');
    expect(next.logs[249].message).toBe('line 249');
  });

  it('trims a batch that overflows maxLogs, keeping the newest', () => {
    const state = { ...emptyState, maxLogs: 10 };
    const next = reducer(state as any, { type: 'AGENT_OUTPUT_BATCH', batch: outputs(25) });
    expect(next.logs).toHaveLength(10);
    expect(next.logs[9].message).toBe('line 24');
  });

  it('returns the same state for an empty batch so no render is scheduled', () => {
    const next = reducer(emptyState as any, { type: 'AGENT_OUTPUT_BATCH', batch: [] });
    expect(next).toBe(emptyState);
  });

  it('maps stderr to Error severity', () => {
    const next = reducer(emptyState as any, {
      type: 'AGENT_OUTPUT_BATCH',
      batch: [{ agentName: 'jvgr1', line: 'boom', kind: 'stderr', sessionId: 's1' }],
    });
    expect(next.logs[0].severity).toBe('Error');
  });
});
