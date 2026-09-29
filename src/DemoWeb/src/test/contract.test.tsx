import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { askQuestion, loadCatalog } from '../api';
import { availability } from '../availability';
import { Results } from '../Results';
import { RequestGate } from '../requestGate';
import { catalog, jsonResponse, result, streamResponse, successfulFrames } from './fixtures';

describe('API contract', () => {
  it('loads the shared backend catalog contract', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse(catalog)));
    expect((await loadCatalog(new AbortController().signal)).identities).toHaveLength(6);
  });

  it.each(['wrong-user', 'wrong-question', 'row-count', 'numeric-cell', 'not-llm'])('rejects %s responses', async (issue) => {
    const response = result();
    const payload = issue === 'wrong-user' ? { ...response, userId: 'app-user-B1' }
      : issue === 'wrong-question' ? { ...response, questionId: 'details' }
      : issue === 'row-count' ? { ...response, trace: { ...response.trace, resultRowCount: 2 } }
      : issue === 'numeric-cell' ? { ...response, data: { ...response.data, rows: [[250]] } }
      : { ...response, answerSource: 'fallback' };
    vi.stubGlobal('fetch', vi.fn(async () => streamResponse(successfulFrames(payload))));
    await expect(askQuestion('app-user-A1', 'overview', new AbortController().signal)).rejects.toThrow();
  });

  it('makes unavailable transport explicit', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => { throw new TypeError('Network unavailable'); }));
    await expect(loadCatalog(new AbortController().signal)).rejects.toMatchObject({ code: 'backend_unavailable' });
  });

  it('preserves large decimals, blanks and strings and never renders model text as HTML', () => {
    const response = result();
    response.answer = '<img src=x onerror=alert(1)>';
    response.data.rows = [['9007199254740993.1234'], [null], ['']];
    response.trace.resultRowCount = 3;
    const view = render(<Results result={response} identity={catalog.identities[0]} />);
    expect(screen.getByText('9007199254740993.1234')).toBeInTheDocument();
    expect(screen.getByLabelText('Null value')).toBeInTheDocument();
    expect(screen.getByLabelText('Empty string')).toBeInTheDocument();
    expect(view.container.querySelector('img')).toBeNull();
    expect(screen.getByText(response.answer)).toBeInTheDocument();
  });
});

describe('live window and request lifetime', () => {
  it.each([
    { liveEnabled: false }, { llmModel: null }, { liveUntilUtc: null },
    { liveUntilUtc: '2000-01-01T00:00:00Z' }, { identities: [] }, { questions: [] },
  ])('disables incomplete or expired configuration: %j', (change) => {
    expect(availability({ ...catalog, ...change }, Date.now()).enabled).toBe(false);
  });

  it('invalidates all earlier tickets and aborts their transports', () => {
    const gate = new RequestGate();
    const first = gate.begin();
    const second = gate.begin();
    expect(first.isCurrent()).toBe(false);
    expect(first.signal.aborted).toBe(true);
    expect(second.isCurrent()).toBe(true);
    gate.invalidate();
    expect(second.isCurrent()).toBe(false);
  });
});
