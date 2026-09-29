import { describe, expect, it, vi } from 'vitest';
import { askQuestion, executionSteps, loadCatalog, streamLimits } from '../api';
import type { ExecutionEvent } from '../api';
import { catalog, controlledStream, executionEvent, jsonResponse, result, streamResponse, successfulFrames } from './fixtures';

function ask(response: Response, onEvent?: (event: ExecutionEvent) => void, signal = new AbortController().signal) {
  vi.stubGlobal('fetch', vi.fn(async () => response));
  return askQuestion('app-user-A1', 'overview', signal, onEvent);
}

function rawStream(text: string): Response {
  return new Response(text, { headers: { 'Content-Type': 'application/x-ndjson' } });
}

describe('real NDJSON stream parsing', () => {
  it('accepts the UTC offset emitted by .NET DateTimeOffset', async () => {
    const frames = successfulFrames();
    frames[0] = { type: 'event', event: {
      ...executionEvent(1, 'identity', 'completed'),
      timestampUtc: '2026-09-28T23:46:40.9803789+00:00',
    } };
    await expect(ask(streamResponse(frames))).resolves.toEqual(result());
  });

  it('incrementally decodes split multibyte UTF-8, split lines, CRLF and a final line without newline', async () => {
    const payload = result();
    payload.answer = 'Synthetic UTF-8: café 日本語 🧪';
    const frames = successfulFrames(payload);
    const first = { type: 'event', event: executionEvent(1, 'identity', 'completed', { value: 'café 日本語 🧪' }) };
    frames[0] = first;
    const bytes = new TextEncoder().encode(frames.map(frame => JSON.stringify(frame)).join('\r\n'));
    let offset = 0;
    const response = new Response(new ReadableStream<Uint8Array>({
      pull(controller) {
        if (offset === bytes.length) controller.close();
        else controller.enqueue(bytes.slice(offset, ++offset));
      },
    }), { headers: { 'Content-Type': 'application/x-ndjson' } });
    const onEvent = vi.fn();
    await expect(ask(response, onEvent)).resolves.toEqual(payload);
    expect(onEvent).toHaveBeenCalledTimes(9);
    expect(onEvent).toHaveBeenNthCalledWith(1, first.event);
    expect(fetch).toHaveBeenCalledWith('/api/ask', expect.objectContaining({
      method: 'POST', credentials: 'omit', cache: 'no-store',
      body: JSON.stringify({ userId: 'app-user-A1', questionId: 'overview' }),
      headers: { Accept: 'application/x-ndjson', 'Content-Type': 'application/json', 'X-Demo-Request': '1' },
    }));
  });

  it('reports events as they arrive but holds a valid result until EOF', async () => {
    const stream = controlledStream();
    const onEvent = vi.fn();
    const resolved = vi.fn();
    const pending = ask(stream.response, onEvent).then(resolved);
    stream.send(successfulFrames()[0]);
    await vi.waitFor(() => expect(onEvent).toHaveBeenCalledTimes(1));
    expect(resolved).not.toHaveBeenCalled();
    for (const frame of successfulFrames().slice(1)) stream.send(frame);
    await vi.waitFor(() => expect(onEvent).toHaveBeenCalledTimes(9));
    expect(resolved).not.toHaveBeenCalled();
    stream.close();
    await pending;
    expect(resolved).toHaveBeenCalledWith(result());
  });

  it('surfaces a backend error after HTTP 200 while retaining earlier event callbacks', async () => {
    const onEvent = vi.fn();
    const frames = [
      { type: 'event', event: executionEvent(1) },
      { type: 'event', event: executionEvent(2, 'iq-auth', 'failed', { reason: 'Synthetic auth denied' }) },
      { type: 'error', error: 'iq_auth_failed', message: 'Synthetic failure.', requestId: 'test-request' },
    ];
    await expect(ask(streamResponse(frames), onEvent)).rejects.toMatchObject({
      code: 'iq_auth_failed', message: 'Synthetic failure.', requestId: 'test-request', status: 200,
    });
    expect(onEvent).toHaveBeenCalledTimes(2);
  });

  it('accepts request-level failure after a completed stage without rewriting its status', async () => {
    const onEvent = vi.fn();
    const completed = successfulFrames().slice(0, -1);
    await expect(ask(streamResponse([
      ...completed,
      { type: 'event', event: executionEvent(10, 'request', 'failed', { reason: 'Synthetic post-stage failure' }) },
      { type: 'error', error: 'request_failed', message: 'Synthetic post-stage failure', requestId: 'test-request' },
    ]), onEvent)).rejects.toMatchObject({ code: 'request_failed', requestId: 'test-request' });
    expect(onEvent).toHaveBeenCalledTimes(10);
    expect(onEvent.mock.calls[8][0]).toMatchObject({ step: 'complete', status: 'completed' });
    expect(onEvent.mock.calls[9][0]).toMatchObject({ step: 'request', status: 'failed' });
  });

  it('retains ordinary non-2xx JSON errors before streaming begins', async () => {
    await expect(ask(jsonResponse({
      error: 'live_disabled', message: 'Synthetic disabled configuration.', requestId: 'test-request',
    }, 403))).rejects.toMatchObject({ code: 'live_disabled', status: 403, requestId: 'test-request' });
  });

  it('rejects a buffered JSON success even if its result would be valid', async () => {
    await expect(ask(jsonResponse(result()))).rejects.toMatchObject({ code: 'invalid_response' });
  });

  it.each(['', '{}\n', '{bad json}\n', '\n', '{"type":"heartbeat"}\n', '{"type":"event"}\n', '{"type":"error"}\n'])(
    'rejects missing or malformed frames: %j', async text => {
      await expect(ask(rawStream(text))).rejects.toMatchObject({ code: 'invalid_response' });
    },
  );

  it('rejects unexpected EOF after otherwise valid progress', async () => {
    const onEvent = vi.fn();
    await expect(ask(streamResponse([{ type: 'event', event: executionEvent() }]), onEvent))
      .rejects.toMatchObject({ code: 'invalid_response', requestId: 'test-request' });
    expect(onEvent).toHaveBeenCalledTimes(1);
  });

  it.each([
    { sequence: 1.5 }, { sequence: -1 }, { requestId: '' }, { step: 'invented-step' },
    { status: 'planned' }, { status: ['completed'] }, { step: 'request', status: 'started' }, { operation: '' }, { identity: null },
    { timestampUtc: 'not-a-date' }, { timestampUtc: '2026-09-28T20:00:00' },
    { elapsedMs: -1 }, { details: { tokenCount: 3 } }, { details: [] },
  ])('rejects malformed event fields: %j', async change => {
    const onEvent = vi.fn();
    await expect(ask(streamResponse([{ type: 'event', event: { ...executionEvent(), ...change } }]), onEvent))
      .rejects.toMatchObject({ code: 'invalid_response' });
    expect(onEvent).not.toHaveBeenCalled();
  });

  it.each([
    { metadataMode: 'prepared-question-catalog-no-live-iq' },
    { generationInputTokens: -1 }, { generationOutputTokens: 1.5 }, { generationMs: -1 },
    { generationModel: '' }, { schemaHash: null }, { queryHash: 42 },
    { customData: 'app-user-B1' },
  ])('rejects malformed or mismatched generated-query trace: %j', async change => {
    const payload = result();
    await expect(ask(streamResponse(successfulFrames({ ...payload, trace: { ...payload.trace, ...change } }))))
      .rejects.toThrow();
  });

  it.each(['event', 'result', 'error'])('rejects request ID drift in a %s frame', async type => {
    const frames = type === 'event'
      ? [
        { type: 'event', event: executionEvent(1) },
        { type: 'event', event: { ...executionEvent(2, 'iq-auth'), requestId: 'other-request' } },
      ]
      : type === 'result'
        ? successfulFrames({ ...result(), requestId: 'other-request' })
        : [
          { type: 'event', event: executionEvent(1) },
          { type: 'error', error: 'failed', message: 'Synthetic failure', requestId: 'other-request' },
        ];
    await expect(ask(streamResponse(frames))).rejects.toMatchObject({ code: 'invalid_response', requestId: 'test-request' });
  });

  it.each([0, 1])('rejects repeated or decreasing sequence numbers: %s', async sequence => {
    await expect(ask(streamResponse([
      { type: 'event', event: executionEvent(1) },
      { type: 'event', event: executionEvent(sequence, 'iq-auth') },
    ]))).rejects.toMatchObject({ code: 'invalid_response' });
  });

  it.each(['started', 'completed'] as const)('rejects duplicate %s stage events', async status => {
    await expect(ask(streamResponse([
      { type: 'event', event: executionEvent(1, 'identity', status) },
      { type: 'event', event: executionEvent(2, 'identity', status) },
    ]))).rejects.toMatchObject({ code: 'invalid_response' });
  });

  it('accepts actual initialization notification and paginated tool discovery operations', async () => {
    let sequence = 0;
    const events: ExecutionEvent[] = [];
    const operation = (step: ExecutionEvent['step'], name: string) => {
      events.push(
        { ...executionEvent(++sequence, step, 'started'), operation: name },
        { ...executionEvent(++sequence, step, 'completed'), operation: name },
      );
    };
    operation('identity', 'Resolve synthetic identity');
    operation('iq-auth', 'Acquire synthetic delegated token');
    operation('iq-initialize', 'initialize');
    operation('iq-initialize', 'notifications/initialized');
    for (let page = 1; page <= 4; page++) operation('iq-tools', `tools/list page ${page}`);
    operation('iq-schema', 'tools/call get_schema');
    operation('dax-generation', 'Generate synthetic DAX');
    operation('rls-query', 'Execute synthetic query');
    operation('explanation', 'Explain synthetic rows');
    operation('complete', 'Complete execution');
    const onEvent = vi.fn();
    await expect(ask(streamResponse([
      ...events.map(event => ({ type: 'event', event })),
      { type: 'result', result: result() },
    ]), onEvent)).resolves.toEqual(result());
    expect(onEvent).toHaveBeenCalledTimes(events.length);
    expect(onEvent.mock.calls.filter(([event]) => event.step === 'iq-tools' && event.status === 'completed')).toHaveLength(4);
  });

  it.each(['iq-initialize', 'iq-tools'] as const)('requires a new started event before another terminal event for %s', async step => {
    await expect(ask(streamResponse([
      { type: 'event', event: executionEvent(1, step, 'started') },
      { type: 'event', event: executionEvent(2, step, 'completed') },
      { type: 'event', event: executionEvent(3, step, 'completed') },
    ]))).rejects.toMatchObject({ code: 'invalid_response' });
  });

  it('allows further observed operation pairs under any nonterminal stage without fixed cardinality', async () => {
    let sequence = 0;
    const frames = executionSteps.flatMap(step => {
      const operations = step === 'iq-schema' ? 3 : 1;
      return Array.from({ length: operations }, (_, index) => [
        { type: 'event', event: { ...executionEvent(++sequence, step, 'started'), operation: `Synthetic ${step} ${index}` } },
        { type: 'event', event: { ...executionEvent(++sequence, step, 'completed'), operation: `Synthetic ${step} ${index}` } },
      ]).flat();
    });
    await expect(ask(streamResponse([...frames, { type: 'result', result: result() }]))).resolves.toEqual(result());
  });

  it('does not turn skipped stages into successful completion', async () => {
    await expect(ask(streamResponse([
      { type: 'event', event: executionEvent(1) },
      { type: 'event', event: executionEvent(2, 'complete') },
      { type: 'result', result: result() },
    ]))).rejects.toMatchObject({ code: 'invalid_response' });
  });

  it('rejects result frames without a completion event', async () => {
    await expect(ask(streamResponse([
      ...successfulFrames().slice(0, -2), { type: 'result', result: result() },
    ]))).rejects.toMatchObject({ code: 'invalid_response' });
  });

  it('never accepts a result after a failed stage', async () => {
    const frames = successfulFrames();
    frames[4] = { type: 'event', event: executionEvent(5, 'iq-schema', 'failed') };
    await expect(ask(streamResponse(frames))).rejects.toMatchObject({ code: 'invalid_response' });
  });

  it.each([
    { type: 'event', event: executionEvent(10) },
    { type: 'result', result: result() },
    { type: 'error', error: 'late_error', message: 'Synthetic late failure.', requestId: 'test-request' },
  ])('rejects any late frame after a valid result instead of releasing the result: %j', async frame => {
    await expect(ask(streamResponse([...successfulFrames(), frame]))).rejects.toMatchObject({ code: 'invalid_response' });
  });

  it('rejects events after a terminal error', async () => {
    await expect(ask(streamResponse([
      { type: 'error', error: 'failed', message: 'Synthetic failure', requestId: 'test-request' },
      { type: 'event', event: executionEvent() },
    ]))).rejects.toMatchObject({ code: 'invalid_response' });
  });

  it('rejects invalid UTF-8 rather than replacing bytes', async () => {
    const response = new Response(new Uint8Array([0xff, 0x0a]), { headers: { 'Content-Type': 'application/x-ndjson' } });
    await expect(ask(response)).rejects.toMatchObject({ code: 'invalid_response' });
  });

  it('retains events when transport fails midstream', async () => {
    const stream = controlledStream();
    const onEvent = vi.fn();
    const pending = expect(ask(stream.response, onEvent)).rejects.toMatchObject({
      code: 'invalid_response', requestId: 'test-request',
    });
    stream.send({ type: 'event', event: executionEvent() });
    await vi.waitFor(() => expect(onEvent).toHaveBeenCalledTimes(1));
    stream.fail();
    await pending;
  });

  it('enforces the line byte bound even without a newline', async () => {
    await expect(ask(rawStream('x'.repeat(streamLimits.lineBytes + 1)))).rejects.toThrow('line exceeded');
  });

  it('enforces total bytes independently of valid, individually bounded lines', async () => {
    const frames = executionSteps.map((step, index) => ({
      type: 'event', event: executionEvent(index + 1, step, 'completed', {
        syntheticPadding: 'x'.repeat(streamLimits.lineBytes - 4096),
      }),
    }));
    await expect(ask(streamResponse(frames))).rejects.toThrow('total byte limit');
  });

  it('cancels a malformed stream reader instead of continuing to read', async () => {
    const cancel = vi.fn();
    let sent = false;
    const response = new Response(new ReadableStream<Uint8Array>({
      pull(controller) {
        if (!sent) {
          sent = true;
          controller.enqueue(new TextEncoder().encode('{"type":"unknown"}\n'));
        }
      },
      cancel,
    }), { headers: { 'Content-Type': 'application/x-ndjson' } });
    await expect(ask(response)).rejects.toMatchObject({ code: 'invalid_response' });
    expect(cancel).toHaveBeenCalledTimes(1);
  });

  it('aborts a waiting reader and does not deliver further events', async () => {
    const stream = controlledStream();
    const controller = new AbortController();
    const onEvent = vi.fn();
    const pending = expect(ask(stream.response, onEvent, controller.signal)).rejects.toMatchObject({ name: 'AbortError' });
    stream.send({ type: 'event', event: executionEvent() });
    await vi.waitFor(() => expect(onEvent).toHaveBeenCalledTimes(1));
    controller.abort();
    await pending;
    expect(onEvent).toHaveBeenCalledTimes(1);
  });

  it('projects only the event contract into diagnostics', async () => {
    const frames = successfulFrames();
    frames[0] = { type: 'event', event: { ...executionEvent(), unrecognizedField: 'synthetic-extra-value' } };
    const onEvent = vi.fn();
    await ask(streamResponse(frames), onEvent);
    expect(onEvent.mock.calls[0][0]).not.toHaveProperty('unrecognizedField');
  });

  it('rejects catalog contracts for the retired prepared-DAX mode', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => jsonResponse({ ...catalog, executionMode: 'prepared-dax' })));
    await expect(loadCatalog(new AbortController().signal)).rejects.toMatchObject({ code: 'invalid_response' });
  });
});
