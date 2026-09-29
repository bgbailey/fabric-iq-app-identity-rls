import { describe, expect, it } from 'vitest';
import { ApiError, readChatStream } from '../api';
import type { ChatResult } from '../api';

function stream(frames: unknown[]): Response {
  return new Response(frames.map(frame => JSON.stringify(frame)).join('\n') + '\n', {
    headers: { 'Content-Type': 'application/x-ndjson' },
  });
}

const result: ChatResult = {
  answer: 'The answer is 42.',
  toolCalls: [{ name: 'execute_dax', arguments: '{"query":"EVALUATE"}', dax: 'EVALUATE ROW("Answer", 42)', rowCount: 1, isError: false }],
  table: { columns: [{ name: '[Answer]', type: 'Int64' }], rows: [['42']] },
  usage: { model: 'test-model', modelCalls: 1, inputTokens: 10, outputTokens: 6 },
};

describe('NDJSON chat stream', () => {
  it('dispatches event frames and returns the result frame', async () => {
    const events: string[] = [];
    const parsed = await readChatStream(stream([
      { type: 'event', event: { step: 'tool', status: 'completed', name: 'execute_dax', elapsedMs: 12, details: { dax: 'EVALUATE ROW("Answer", 42)', rowCount: '1' } } },
      { type: 'result', result },
    ]), event => events.push(`${event.step}:${event.name}`));

    expect(events).toEqual(['tool:execute_dax']);
    expect(parsed).toEqual(result);
  });

  it('throws for error frames', async () => {
    await expect(readChatStream(stream([{ type: 'error', message: 'No access' }]), () => undefined)).rejects.toBeInstanceOf(ApiError);
  });
});
