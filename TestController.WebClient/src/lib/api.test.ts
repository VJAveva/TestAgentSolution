import { describe, it, expect } from 'vitest';
import { apiErrorMessage } from './api';

describe('apiErrorMessage', () => {
  it('surfaces the server detail from an apiFetch rejection', () => {
    // apiFetch rejects with a plain object, so `err instanceof Error` is false. Callers used to fall through
    // to their generic fallback and discard exactly this text.
    const thrown = {
      status: 500,
      error: 'Internal Server Error',
      detail: '[3235ee1a] ADO auth failed (401). Credential: PAT (Basic auth).',
      correlationId: 'f8s7cg7l',
    };

    expect(apiErrorMessage(thrown, 'Failed to load regression data'))
      .toBe('[3235ee1a] ADO auth failed (401). Credential: PAT (Basic auth). (HTTP 500)');
  });

  it('falls back to the error field when there is no detail', () => {
    expect(apiErrorMessage({ status: 403, error: 'Permission denied' }, 'nope'))
      .toBe('Permission denied (HTTP 403)');
  });

  it('keeps the correlation id when the body carries no message', () => {
    expect(apiErrorMessage({ correlationId: 'abc123' }, 'Failed to load'))
      .toBe('Failed to load [abc123]');
  });

  it('uses the message of a real Error', () => {
    expect(apiErrorMessage(new Error('boom'), 'fallback')).toBe('boom');
  });

  it('returns the fallback for values it cannot interpret', () => {
    expect(apiErrorMessage(undefined, 'fallback')).toBe('fallback');
    expect(apiErrorMessage('a string', 'fallback')).toBe('fallback');
    expect(apiErrorMessage({}, 'fallback')).toBe('fallback');
  });
});
