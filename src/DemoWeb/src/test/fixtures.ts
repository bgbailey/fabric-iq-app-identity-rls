import contract from '../../../../docs/demo-api.json';
import { executionSteps } from '../api';
import type { AskResult, DemoCatalog, ExecutionEvent } from '../api';

export const catalog: DemoCatalog = {
  ...contract.catalog.response,
  mode: 'technical-proof',
  executionMode: 'fabric-iq-generated-dax',
  liveEnabled: true,
  liveUntilUtc: '2099-01-01T00:00:00Z',
  llmModel: 'synthetic-test-model',
};

export function result(userId = 'app-user-A1', questionId = 'overview'): AskResult {
  return {
    requestId: 'test-request',
    userId,
    questionId,
    question: catalog.questions.find(q => q.id === questionId)?.text ?? '',
    answer: 'Test-only explanation from the stubbed service.',
    answerSource: 'llm',
    data: {
      columns: [{ name: 'Total', arrowType: 'decimal128(19,4)', nullable: true }],
      rows: [['250.0000']],
    },
    trace: {
      role: 'ExternalAppScope', customData: userId,
      query: 'EVALUATE ROW("Total", [Total Amount])', queryHash: 'a'.repeat(64),
      queryMs: 10, llmMs: 20, resultRowCount: 1,
      inputTokens: 11, outputTokens: 7, model: 'synthetic-test-model',
      metadataMode: 'live-fabric-iq-schema',
      schemaHash: 'b'.repeat(64), generationModel: 'synthetic-test-model',
      generationInputTokens: 19, generationOutputTokens: 13, generationMs: 15,
    },
  };
}

export function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

export function executionEvent(
  sequence = 1,
  step: ExecutionEvent['step'] = 'identity',
  status: ExecutionEvent['status'] = 'completed',
  details: Record<string, string> = {},
): ExecutionEvent {
  return {
    sequence, requestId: 'test-request', step, status,
    operation: `Synthetic test operation: ${step}`, identity: 'Synthetic test identity',
    timestampUtc: '2026-09-28T20:00:00.000Z', elapsedMs: sequence * 10, details,
  };
}

export function successfulFrames(payload: unknown = result()): unknown[] {
  return [
    ...executionSteps.map((step, index) => ({ type: 'event', event: executionEvent(index + 1, step) })),
    { type: 'result', result: payload },
  ];
}

export function streamResponse(frames: unknown[] = successfulFrames()): Response {
  return new Response(frames.map(frame => JSON.stringify(frame)).join('\n') + '\n', {
    headers: { 'Content-Type': 'application/x-ndjson; charset=utf-8' },
  });
}

export function controlledStream() {
  let controller!: ReadableStreamDefaultController<Uint8Array>;
  const encoder = new TextEncoder();
  const response = new Response(new ReadableStream<Uint8Array>({
    start(value) { controller = value; },
  }), { headers: { 'Content-Type': 'application/x-ndjson' } });
  return {
    response,
    send(frame: unknown) { controller.enqueue(encoder.encode(JSON.stringify(frame) + '\n')); },
    sendBytes(bytes: Uint8Array) { controller.enqueue(bytes); },
    close() { controller.close(); },
    fail() { controller.error(new TypeError('Synthetic test transport failure')); },
  };
}
