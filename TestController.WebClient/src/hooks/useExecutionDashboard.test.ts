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
  dismissed: new Set<string>(),
  demoOn: false,
};

const session = (sessionId: string, status = 'Running', extra: any = {}) => ({
  sessionId, watchItemTag: sessionId, userId: 'u', source: 'WPF', status,
  startedUtc: '2026-10-04T00:00:00Z', elapsed: '00:01', agents: [],
  totalActions: 0, completedActions: 0, passedActions: 0, failedActions: 0,
  progressPercent: 0, lockedAgents: [], ...extra,
});

const stateWith = (...sessions: any[]) => ({
  ...emptyState,
  sessions: new Map(sessions.map(s => [s.sessionId, s])),
});

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

/**
 * Demo used to dispatch SET_SESSIONS, whose reducer rebuilds the Map from the payload alone —
 * so turning Demo on deleted every real run, and the next poll deleted every demo card back.
 */
describe('useExecutionDashboard demo toggle', () => {
  it('keeps real sessions when demo is switched on', () => {
    const next = reducer(stateWith(session('real-1')) as any, {
      type: 'SET_DEMO', on: true, sessions: [session('demo-1', 'Success')] as any,
    });

    expect(next.sessions.has('real-1')).toBe(true);
    expect(next.sessions.get('demo-1')!.isDemo).toBe(true);
    expect(next.demoOn).toBe(true);
  });

  it('removes only demo sessions when switched off', () => {
    const on = reducer(stateWith(session('real-1')) as any, {
      type: 'SET_DEMO', on: true, sessions: [session('demo-1', 'Success')] as any,
    });

    const off = reducer(on, { type: 'SET_DEMO', on: false });

    expect([...off.sessions.keys()]).toEqual(['real-1']);
    expect(off.demoOn).toBe(false);
  });

  it('survives a poll, because SET_SESSIONS preserves demo cards', () => {
    const on = reducer(stateWith(session('real-1')) as any, {
      type: 'SET_DEMO', on: true, sessions: [session('demo-1', 'Success')] as any,
    });

    const polled = reducer(on, { type: 'SET_SESSIONS', sessions: [session('real-1')] as any });

    expect(polled.sessions.has('demo-1')).toBe(true);
    expect(polled.sessions.has('real-1')).toBe(true);
  });
});

describe('useExecutionDashboard clear finished', () => {
  it('removes terminal sessions and keeps running ones', () => {
    const state = stateWith(
      session('running-1', 'Running'),
      session('done-1', 'Success'),
      session('done-2', 'Failed'),
      session('done-3', 'Cancelled'),
    );

    const next = reducer(state as any, { type: 'CLEAR_FINISHED' });

    expect([...next.sessions.keys()]).toEqual(['running-1']);
  });

  it('does not let a later poll resurrect what was cleared', () => {
    const cleared = reducer(stateWith(session('done-1', 'Success')) as any, { type: 'CLEAR_FINISHED' });

    const polled = reducer(cleared, { type: 'SET_SESSIONS', sessions: [session('done-1', 'Success')] as any });

    expect(polled.sessions.has('done-1')).toBe(false);
  });

  it('shows a cleared session again if it goes back to Running', () => {
    const cleared = reducer(stateWith(session('repeat-1', 'Failed')) as any, { type: 'CLEAR_FINISHED' });

    const polled = reducer(cleared, { type: 'SET_SESSIONS', sessions: [session('repeat-1', 'Running')] as any });

    expect(polled.sessions.has('repeat-1')).toBe(true);
  });

  it('lets a new run appear after a clear', () => {
    const cleared = reducer(stateWith(session('done-1', 'Success')) as any, { type: 'CLEAR_FINISHED' });

    const polled = reducer(cleared, { type: 'SET_SESSIONS', sessions: [session('fresh-1', 'Running')] as any });

    expect(polled.sessions.has('fresh-1')).toBe(true);
  });

  it('drops the selection when the selected card is cleared', () => {
    const state = { ...stateWith(session('done-1', 'Success')), selectedSessionId: 'done-1' };

    const next = reducer(state as any, { type: 'CLEAR_FINISHED' });

    expect(next.selectedSessionId).toBeNull();
  });
});
