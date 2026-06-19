import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';

// Mock the apiFetch layer (the hook calls apiGet)
vi.mock('../lib/api', () => ({ apiGet: vi.fn() }));
import { apiGet } from '../lib/api';
// Alias so existing `mockedAxios.get` call-sites keep reading naturally.
const mockedAxios = { get: vi.mocked(apiGet) };

// Mock connectionStore
const mockStatus = { value: 'disconnected' };
vi.mock('../stores/connectionStore', () => ({
  useConnectionStore: (selector: any) => selector({ status: mockStatus.value }),
}));

// Mock errorThrottle
vi.mock('../lib/errorThrottle', () => ({
  errorThrottle: {
    shouldReport: vi.fn(() => true),
    reset: vi.fn(),
  },
}));

import { useAgentTelemetry } from './useAgentTelemetry';

describe('useAgentTelemetry — timer lifecycle', () => {
  beforeEach(() => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.clearAllMocks();
    mockStatus.value = 'disconnected';
    mockedAxios.get.mockImplementation(() =>
      Promise.resolve({
        agentName: 'TestAgent',
        state: 'Ready',
        currentActivity: '',
        currentCommand: '',
        executionsCompleted: 5,
        executionsFailed: 0,
        cpuUsagePct: 25,
        memoryUsedMb: 1024,
        memoryTotalMb: 4096,
        diskFreeGb: 50,
        activeProcessCount: 3,
        timestamp: new Date().toISOString(),
      })
    );
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('calls fetch on mount when agentName is provided', async () => {
    await act(async () => {
      renderHook(() => useAgentTelemetry('TestAgent'));
    });

    expect(mockedAxios.get).toHaveBeenCalledWith('/api/agents/TestAgent/telemetry');
  });

  it('does not fetch when agentName is null', async () => {
    await act(async () => {
      renderHook(() => useAgentTelemetry(null));
    });

    expect(mockedAxios.get).not.toHaveBeenCalled();
  });

  it('sets up interval timer that fires periodically', async () => {
    await act(async () => {
      renderHook(() => useAgentTelemetry('TestAgent'));
    });

    const initialCalls = mockedAxios.get.mock.calls.length;

    // Advance past the default interval (2s)
    await act(async () => {
      vi.advanceTimersByTime(2100);
    });

    expect(mockedAxios.get.mock.calls.length).toBeGreaterThan(initialCalls);
  });

  it('cleans up timer on unmount (no timer leak)', async () => {
    let hookResult: any;
    await act(async () => {
      hookResult = renderHook(() => useAgentTelemetry('TestAgent'));
    });

    hookResult.unmount();

    const callsAtUnmount = mockedAxios.get.mock.calls.length;

    // Advance time — should not trigger more calls
    await act(async () => {
      vi.advanceTimersByTime(10000);
    });

    expect(mockedAxios.get.mock.calls.length).toBe(callsAtUnmount);
  });

  it('cleans up timer when agentName becomes null', async () => {
    let hookResult: any;
    await act(async () => {
      hookResult = renderHook(
        ({ name }: { name: string | null }) => useAgentTelemetry(name),
        { initialProps: { name: 'TestAgent' } }
      );
    });

    // Change to null
    await act(async () => {
      hookResult.rerender({ name: null });
    });

    const callsAfterNull = mockedAxios.get.mock.calls.length;

    await act(async () => {
      vi.advanceTimersByTime(10000);
    });

    expect(mockedAxios.get.mock.calls.length).toBe(callsAfterNull);
  });

  it('fetches for new agent when agentName changes', async () => {
    let hookResult: any;
    await act(async () => {
      hookResult = renderHook(
        ({ name }: { name: string | null }) => useAgentTelemetry(name),
        { initialProps: { name: 'Agent1' } }
      );
    });

    expect(mockedAxios.get).toHaveBeenCalledWith('/api/agents/Agent1/telemetry');

    await act(async () => {
      hookResult.rerender({ name: 'Agent2' });
    });

    expect(mockedAxios.get).toHaveBeenCalledWith('/api/agents/Agent2/telemetry');
  });

  it('sets error state on fetch failure', async () => {
    mockedAxios.get.mockImplementationOnce(() =>
      Promise.reject(new Error('Network Error')));

    let hookResult: any;
    await act(async () => {
      hookResult = renderHook(() => useAgentTelemetry('TestAgent'));
    });

    expect(hookResult.result.current.error).toBe('Network Error');
  });

  it('clears error on successful fetch after failure', async () => {
    mockedAxios.get
      .mockImplementationOnce(() => Promise.reject(new Error('Network Error')))
      .mockImplementationOnce(() => Promise.resolve(
        { agentName: 'TestAgent', state: 'Ready' },
      ));

    let hookResult: any;
    await act(async () => {
      hookResult = renderHook(() => useAgentTelemetry('TestAgent'));
    });

    expect(hookResult.result.current.error).toBe('Network Error');

    // Advance past interval to trigger second fetch
    await act(async () => {
      vi.advanceTimersByTime(2100);
    });

    expect(hookResult.result.current.error).toBeNull();
  });

  it('uses backoff interval when SignalR is connected', async () => {
    mockStatus.value = 'connected';

    await act(async () => {
      renderHook(() => useAgentTelemetry('TestAgent'));
    });

    const callsAfterMount = mockedAxios.get.mock.calls.length;

    // Advance 2s — should NOT fire (backoff is 15s)
    await act(async () => {
      vi.advanceTimersByTime(2100);
    });

    expect(mockedAxios.get.mock.calls.length).toBe(callsAfterMount);

    // Advance to 15s — should fire
    await act(async () => {
      vi.advanceTimersByTime(13000);
    });

    expect(mockedAxios.get.mock.calls.length).toBeGreaterThan(callsAfterMount);
  });
});
