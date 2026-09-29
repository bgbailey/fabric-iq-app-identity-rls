import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { App } from '../App';
import { catalog, controlledStream, executionEvent, jsonResponse, result, streamResponse, successfulFrames } from './fixtures';

async function openApp(ask: (init: RequestInit) => Promise<Response> = async () => streamResponse()) {
  const fetcher = vi.fn(async (path: string, init: RequestInit) =>
    path === '/api/demo' ? jsonResponse(catalog) : ask(init));
  vi.stubGlobal('fetch', fetcher);
  render(<App />);
  await screen.findByRole('button', { name: /Run selected question/ });
  return fetcher;
}

describe('presenter flow', () => {
  it('loads six identities and five questions without automatically spending cloud calls', async () => {
    const fetcher = await openApp();
    expect(screen.getAllByRole('radio')).toHaveLength(11);
    fireEvent.click(screen.getByRole('radio', { name: 'Customer B - Home' }));
    expect(fetcher).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('button', { name: /Run selected question/ })).toBeEnabled();
    fireEvent.click(screen.getByRole('button', { name: /Run selected question/ }));
    await screen.findByRole('alert');
    expect(fetcher.mock.calls[1][1].body).toBe(JSON.stringify({ userId: 'app-user-B1', questionId: 'overview' }));
    expect(screen.queryByRole('heading', { name: 'LLM explanation' })).not.toBeInTheDocument();
    expect(screen.getByText('response_mismatch')).toBeInTheDocument();
  });

  it('shows exact rows and clears both answer and rows when switching identities', async () => {
    await openApp();
    fireEvent.click(screen.getByRole('button', { name: /Run selected question/ }));
    await screen.findByText('250.0000');
    expect(screen.getByText('Test-only explanation from the stubbed service.')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('radio', { name: 'Customer A - Auto' }));
    expect(screen.queryByText('250.0000')).not.toBeInTheDocument();
    expect(screen.queryByText('Test-only explanation from the stubbed service.')).not.toBeInTheDocument();
    expect(screen.getByRole('radio', { name: /What is my total/ })).toBeChecked();
  });

  it.each(['identity', 'question', 'cancel'])('discards a late answer after %s changes even if transport ignores abort', async (action) => {
    let finish!: (response: Response) => void;
    let requestSignal: AbortSignal | null | undefined;
    await openApp(init => {
      requestSignal = init.signal;
      return new Promise(resolve => { finish = resolve; });
    });
    fireEvent.click(screen.getByRole('button', { name: /Run selected question/ }));
    await screen.findByRole('button', { name: 'Cancel request' });
    if (action === 'identity') fireEvent.click(screen.getByRole('radio', { name: 'Customer B - Home' }));
    if (action === 'question') fireEvent.click(screen.getByRole('radio', { name: /Break down my activity/ }));
    if (action === 'cancel') fireEvent.click(screen.getByRole('button', { name: 'Cancel request' }));
    expect(requestSignal?.aborted).toBe(true);
    await act(async () => { finish(streamResponse()); });
    expect(screen.queryByText('250.0000')).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'LLM explanation' })).not.toBeInTheDocument();
  });

  it('shows a controlled backend error without fabricated results or automatic retry', async () => {
    const fetcher = await openApp(async () => jsonResponse({
      error: 'query_failed', message: 'The semantic model could not be reached.', requestId: 'failed-request',
    }, 502));
    fireEvent.click(screen.getByRole('button', { name: /Run selected question/ }));
    await screen.findByRole('alert');
    expect(screen.getByText('query_failed')).toBeInTheDocument();
    expect(screen.getAllByText('failed-request').length).toBeGreaterThan(0);
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    expect(fetcher).toHaveBeenCalledTimes(2);
  });

  it('does not execute when live access is disabled', async () => {
    const fetcher = vi.fn(async () => jsonResponse({ ...catalog, liveEnabled: false }));
    vi.stubGlobal('fetch', fetcher);
    render(<App />);
    await waitFor(() => expect(screen.getByRole('button', { name: /Run selected question/ })).toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: /Run selected question/ }));
    expect(fetcher).toHaveBeenCalledTimes(1);
  });

  it('shows real events while running and never completes stages that have not run after failure', async () => {
    const stream = controlledStream();
    const fetcher = await openApp(async () => stream.response);
    fireEvent.click(screen.getByRole('button', { name: /Run selected question/ }));
    await act(async () => {
      stream.send({ type: 'event', event: executionEvent(1) });
      stream.send({ type: 'event', event: executionEvent(2, 'iq-auth', 'started', { resource: 'synthetic-iq-resource' }) });
    });
    const stages = screen.getByRole('list', { name: 'Backend execution stages' });
    expect(within(stages).getAllByText('NOT RUN')).toHaveLength(7);
    expect(within(stages).getAllByText('COMPLETED')).toHaveLength(1);
    expect(within(stages).getAllByText(/elapsedMs:/).length).toBeGreaterThan(0);
    await act(async () => {
      stream.send({ type: 'event', event: executionEvent(3, 'iq-auth', 'failed', { reason: 'Synthetic delegated auth failure' }) });
      stream.send({ type: 'error', error: 'iq_auth_failed', message: 'Synthetic auth failed.', requestId: 'test-request' });
      stream.close();
    });
    await screen.findByRole('alert');
    expect(within(stages).getByText('FAILED')).toBeInTheDocument();
    expect(within(stages).getAllByText('NOT RUN')).toHaveLength(7);
    expect(within(stages).getAllByText('COMPLETED')).toHaveLength(1);
    expect(screen.getByLabelText('Complete plaintext event log')).toHaveTextContent('Synthetic delegated auth failure');
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
    expect(fetcher).toHaveBeenCalledTimes(2);
  });

  it('retains partial schema and generated DAX on browser cancellation, without faking a backend cancelled event', async () => {
    const stream = controlledStream();
    await openApp(async () => stream.response);
    fireEvent.click(screen.getByRole('button', { name: /Run selected question/ }));
    await act(async () => {
      stream.send({ type: 'event', event: executionEvent(1, 'iq-schema', 'completed', { schema: 'Synthetic schema before cancellation' }) });
      stream.send({ type: 'event', event: executionEvent(2, 'dax-generation', 'completed', { generatedDax: 'Synthetic DAX before cancellation' }) });
      stream.send({ type: 'event', event: executionEvent(3, 'rls-query', 'started') });
    });
    fireEvent.click(screen.getByRole('button', { name: 'Cancel request' }));
    expect(screen.getByText('STARTED · UNCONFIRMED')).toBeInTheDocument();
    const log = screen.getByLabelText('Complete plaintext event log');
    expect(log).toHaveTextContent('Synthetic schema before cancellation');
    expect(log).toHaveTextContent('Synthetic DAX before cancellation');
    expect(log).toHaveTextContent('BROWSER CANCELLED');
    expect(log).not.toHaveTextContent('"status":"cancelled"');
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it.each(['identity', 'question', 'refresh'])('clears all diagnostics on %s change', async (change) => {
    const stream = controlledStream();
    await openApp(async () => stream.response);
    fireEvent.click(screen.getByRole('button', { name: /Run selected question/ }));
    await act(async () => stream.send({
      type: 'event', event: executionEvent(1, 'iq-schema', 'completed', { schema: 'synthetic-old-schema' }),
    }));
    expect(screen.getByLabelText('Complete plaintext event log')).toHaveTextContent('synthetic-old-schema');
    if (change === 'identity') fireEvent.click(screen.getByRole('radio', { name: 'Customer B - Home' }));
    if (change === 'question') fireEvent.click(screen.getByRole('radio', { name: /Break down my activity/ }));
    if (change === 'refresh') fireEvent.click(screen.getByRole('button', { name: 'Refresh configuration' }));
    await screen.findByRole('list', { name: 'Backend execution stages' });
    expect(screen.getByLabelText('Complete plaintext event log')).not.toHaveTextContent('synthetic-old-schema');
    expect(screen.getAllByText('NOT RUN')).toHaveLength(9);
  });

  it('discards late events as well as results after selecting another identity, even when fetch ignores abort', async () => {
    let finish!: (response: Response) => void;
    await openApp(() => new Promise(resolve => { finish = resolve; }));
    fireEvent.click(screen.getByRole('button', { name: /Run selected question/ }));
    fireEvent.click(screen.getByRole('radio', { name: 'Customer B - Home' }));
    await act(async () => finish(streamResponse([
      { type: 'event', event: executionEvent(1, 'iq-schema', 'completed', { schema: 'late-old-user-schema' }) },
      ...successfulFrames(),
    ])));
    expect(screen.getByLabelText('Complete plaintext event log')).not.toHaveTextContent('late-old-user-schema');
    expect(screen.getAllByText('NOT RUN')).toHaveLength(9);
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });

  it('renders unsafe event details as escaped text and copies the complete log only on request', async () => {
    const unsafe = '<img src=x onerror=alert(1)>';
    const stream = controlledStream();
    const writeText = vi.fn(async () => undefined);
    vi.stubGlobal('navigator', { clipboard: { writeText } });
    await openApp(async () => stream.response);
    fireEvent.click(screen.getByRole('button', { name: /Run selected question/ }));
    await act(async () => stream.send({
      type: 'event', event: executionEvent(1, 'iq-schema', 'completed', { schema: unsafe }),
    }));
    expect(document.querySelector('img')).toBeNull();
    expect(screen.getByLabelText('iq-schema event 1')).toHaveTextContent(unsafe);
    expect(writeText).not.toHaveBeenCalled();
    fireEvent.click(screen.getByText('Complete event log', { exact: true }));
    fireEvent.click(screen.getByRole('button', { name: 'Copy complete event log' }));
    await waitFor(() => expect(writeText).toHaveBeenCalledWith(screen.getByLabelText('Complete plaintext event log').textContent));
    fireEvent.click(screen.getByRole('button', { name: 'Cancel request' }));
  });

  it('keeps generated-query provenance and the completed timeline visible after a valid stream', async () => {
    await openApp();
    fireEvent.click(screen.getByRole('button', { name: /Run selected question/ }));
    await screen.findByRole('table');
    expect(screen.getByRole('heading', { name: 'Live execution' })).toBeVisible();
    expect(screen.getAllByText('COMPLETED')).toHaveLength(9);
    expect(screen.getByLabelText('Generated DAX query')).toHaveTextContent(result().trace.query);
    expect(screen.getByText(result().trace.schemaHash)).toBeInTheDocument();
    expect(screen.getByText('IQ + DAX planning duration')).toBeInTheDocument();
    expect(screen.queryByText('Prepared DAX')).not.toBeInTheDocument();
  });

  it('shows request-level failures separately without marking completed stages failed', async () => {
    await openApp(async () => streamResponse([
      { type: 'event', event: executionEvent(1) },
      { type: 'event', event: executionEvent(2, 'request', 'failed', { reason: 'Synthetic post-stage failure' }) },
      { type: 'error', error: 'request_failed', message: 'Synthetic post-stage failure', requestId: 'test-request' },
    ]));
    fireEvent.click(screen.getByRole('button', { name: /Run selected question/ }));
    await screen.findByRole('alert');
    const stages = screen.getByRole('list', { name: 'Backend execution stages' });
    expect(within(stages).getAllByText('COMPLETED')).toHaveLength(1);
    expect(within(stages).getAllByText('NOT RUN')).toHaveLength(8);
    expect(within(stages).queryByText('FAILED')).not.toBeInTheDocument();
    expect(within(screen.getByRole('region', { name: 'Request-level diagnostic' })).getByText('FAILED')).toBeInTheDocument();
    expect(screen.getByLabelText('Complete plaintext event log')).toHaveTextContent('"step":"request"');
  });

  it('does not declare a multi-operation stage complete after its first actual operation', async () => {
    const stream = controlledStream();
    await openApp(async () => stream.response);
    fireEvent.click(screen.getByRole('button', { name: /Run selected question/ }));
    await act(async () => {
      stream.send({ type: 'event', event: { ...executionEvent(1, 'iq-initialize', 'started'), operation: 'initialize' } });
      stream.send({ type: 'event', event: { ...executionEvent(2, 'iq-initialize', 'completed'), operation: 'initialize' } });
    });
    const stage = screen.getByRole('heading', { name: 'Initialize Fabric IQ' }).closest('li')!;
    expect(within(stage).getByText('OPERATION COMPLETED · STAGE UNCONFIRMED')).toBeInTheDocument();
    expect(within(stage).queryByText('COMPLETED')).not.toBeInTheDocument();
    await act(async () => {
      stream.send({ type: 'event', event: { ...executionEvent(3, 'iq-initialize', 'started'), operation: 'notifications/initialized' } });
    });
    expect(within(stage).getByText('STARTED')).toBeInTheDocument();
    expect(within(stage).getByText('notifications/initialized')).toBeInTheDocument();
    await act(async () => {
      stream.send({ type: 'event', event: { ...executionEvent(4, 'iq-initialize', 'completed'), operation: 'notifications/initialized' } });
    });
    expect(within(stage).getByText('OPERATION COMPLETED · STAGE UNCONFIRMED')).toBeInTheDocument();
    expect(within(stage).getByText(/2 completed operation/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Cancel request' }));
    expect(within(stage).queryByText('COMPLETED')).not.toBeInTheDocument();
    expect(screen.getByLabelText('Complete plaintext event log')).toHaveTextContent('notifications/initialized');
  });
});
