import { describe, it, expect, beforeEach, vi } from 'vitest';

// We need a fresh instance per test, so import the class module
// and create instances manually rather than using the singleton.
describe('ErrorThrottle', () => {
  let ErrorThrottle: any;

  beforeEach(async () => {
    vi.useFakeTimers();
    // Re-import to get fresh module state
    vi.resetModules();
    const mod = await import('./errorThrottle');
    ErrorThrottle = (mod as any).errorThrottle;
  });

  it('allows first occurrence to be reported', () => {
    expect(ErrorThrottle.shouldReport('telemetry', 'agent1')).toBe(true);
  });

  it('suppresses duplicate within backoff window', () => {
    ErrorThrottle.shouldReport('telemetry', 'agent1');
    expect(ErrorThrottle.shouldReport('telemetry', 'agent1')).toBe(false);
    expect(ErrorThrottle.shouldReport('telemetry', 'agent1')).toBe(false);
  });

  it('allows report again after backoff expires', () => {
    ErrorThrottle.shouldReport('telemetry', 'agent1');

    // Advance past the initial 5s backoff
    vi.advanceTimersByTime(5001);
    expect(ErrorThrottle.shouldReport('telemetry', 'agent1')).toBe(true);
  });

  it('increases backoff exponentially up to max', () => {
    ErrorThrottle.shouldReport('telemetry', 'agent1'); // initial: 5s backoff

    vi.advanceTimersByTime(5001);
    ErrorThrottle.shouldReport('telemetry', 'agent1'); // now 10s backoff

    vi.advanceTimersByTime(5000);
    expect(ErrorThrottle.shouldReport('telemetry', 'agent1')).toBe(false); // still within 10s

    vi.advanceTimersByTime(5001);
    ErrorThrottle.shouldReport('telemetry', 'agent1'); // now 20s backoff

    vi.advanceTimersByTime(20001);
    ErrorThrottle.shouldReport('telemetry', 'agent1'); // now 40s backoff

    vi.advanceTimersByTime(40001);
    ErrorThrottle.shouldReport('telemetry', 'agent1'); // now 60s (capped)

    vi.advanceTimersByTime(60001);
    ErrorThrottle.shouldReport('telemetry', 'agent1'); // still 60s (maxed)

    vi.advanceTimersByTime(30000);
    expect(ErrorThrottle.shouldReport('telemetry', 'agent1')).toBe(false); // within 60s cap
  });

  it('tracks different keys independently', () => {
    ErrorThrottle.shouldReport('telemetry', 'agent1');
    expect(ErrorThrottle.shouldReport('telemetry', 'agent2')).toBe(true);
    expect(ErrorThrottle.shouldReport('fleet', '')).toBe(true);
  });

  it('reset allows immediate re-report', () => {
    ErrorThrottle.shouldReport('telemetry', 'agent1');
    expect(ErrorThrottle.shouldReport('telemetry', 'agent1')).toBe(false);

    ErrorThrottle.reset('telemetry', 'agent1');
    expect(ErrorThrottle.shouldReport('telemetry', 'agent1')).toBe(true);
  });

  it('tracks suppressed count', () => {
    ErrorThrottle.shouldReport('fleet', '');
    ErrorThrottle.shouldReport('fleet', '');
    ErrorThrottle.shouldReport('fleet', '');
    expect(ErrorThrottle.getSuppressedCount('fleet', '')).toBe(2);
  });
});
