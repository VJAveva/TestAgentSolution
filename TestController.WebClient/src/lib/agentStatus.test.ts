import { describe, it, expect } from 'vitest';
import { isAgentOnline, isAgentOffline, classifyAgentHealth } from './agentStatus';

describe('isAgentOnline', () => {
  it('returns true for known online patterns', () => {
    expect(isAgentOnline('Connected')).toBe(true);
    expect(isAgentOnline('Online')).toBe(true);
    expect(isAgentOnline('Healthy')).toBe(true);
    expect(isAgentOnline('AgentStateReady')).toBe(true);
  });

  it('returns true for execution-related statuses', () => {
    expect(isAgentOnline('Executing: deploy.cmd')).toBe(true);
    expect(isAgentOnline('Ready')).toBe(true);
    expect(isAgentOnline('AgentStateRunning')).toBe(true);
    expect(isAgentOnline('Rebooting… waiting for agent')).toBe(true);
    expect(isAgentOnline('Failed (exit 1)')).toBe(true);
    expect(isAgentOnline('Waiting for previous command to finish (3/6)')).toBe(true);
  });

  it('returns true for compound online statuses', () => {
    expect(isAgentOnline('Online · AgentStateReady')).toBe(true);
    expect(isAgentOnline('Online (post-reboot)')).toBe(true);
  });

  it('is case-insensitive', () => {
    expect(isAgentOnline('connected')).toBe(true);
    expect(isAgentOnline('ONLINE')).toBe(true);
    expect(isAgentOnline('EXECUTING: TEST')).toBe(true);
  });

  it('returns false for offline patterns', () => {
    expect(isAgentOnline('Offline')).toBe(false);
    expect(isAgentOnline('Disconnected')).toBe(false);
  });

  it('returns false for null/undefined/empty', () => {
    expect(isAgentOnline(null)).toBe(false);
    expect(isAgentOnline(undefined)).toBe(false);
    expect(isAgentOnline('')).toBe(false);
  });
});

describe('isAgentOffline', () => {
  it('returns true for known offline patterns', () => {
    expect(isAgentOffline('Offline')).toBe(true);
    expect(isAgentOffline('Unhealthy')).toBe(true);
    expect(isAgentOffline('Disconnected')).toBe(true);
    expect(isAgentOffline('Unreachable')).toBe(true);
    expect(isAgentOffline('Error')).toBe(true);
  });

  it('returns true for null/undefined', () => {
    expect(isAgentOffline(null)).toBe(true);
    expect(isAgentOffline(undefined)).toBe(true);
  });

  it('returns false for online patterns', () => {
    expect(isAgentOffline('Connected')).toBe(false);
    expect(isAgentOffline('Online')).toBe(false);
  });
});

describe('classifyAgentHealth', () => {
  it('returns online for known good statuses', () => {
    expect(classifyAgentHealth('Connected')).toBe('online');
    expect(classifyAgentHealth('Healthy')).toBe('online');
  });

  it('returns online for execution-related statuses', () => {
    expect(classifyAgentHealth('Executing: deploy.cmd')).toBe('online');
    expect(classifyAgentHealth('Ready')).toBe('online');
    expect(classifyAgentHealth('Failed (exit 1)')).toBe('online');
    expect(classifyAgentHealth('AgentStateRunning')).toBe('online');
  });

  it('returns offline for known bad statuses', () => {
    expect(classifyAgentHealth('Offline')).toBe('offline');
    expect(classifyAgentHealth('Unreachable')).toBe('offline');
  });

  it('returns offline for stale timestamps with ambiguous status', () => {
    const oldDate = new Date(Date.now() - 120_000).toISOString();
    expect(classifyAgentHealth('Unknown', oldDate)).toBe('offline');
  });

  it('returns unknown for recent timestamp with ambiguous status', () => {
    const recentDate = new Date(Date.now() - 5_000).toISOString();
    expect(classifyAgentHealth('Unknown', recentDate)).toBe('unknown');
  });

  it('returns unknown for ambiguous status with no timestamp', () => {
    expect(classifyAgentHealth('Initializing')).toBe('unknown');
  });
});
