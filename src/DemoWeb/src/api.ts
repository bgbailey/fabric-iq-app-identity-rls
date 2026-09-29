export interface DemoIdentity {
  id: string;
  label: string;
  scope: string;
}

export interface PreparedQuestion {
  id: string;
  text: string;
  purpose: string;
}

export interface DemoCatalog {
  mode: 'technical-proof';
  executionMode: 'fabric-iq-generated-dax';
  liveEnabled: boolean;
  liveUntilUtc: string | null;
  llmModel: string | null;
  identityNotice: string;
  identities: DemoIdentity[];
  questions: PreparedQuestion[];
  evidenceNote: string;
}

export interface ResultColumn {
  name: string;
  arrowType: string;
  nullable: boolean;
}

export interface AskResult {
  requestId: string;
  userId: string;
  questionId: string;
  question: string;
  answer: string;
  answerSource: 'llm';
  data: {
    columns: ResultColumn[];
    rows: (string | null)[][];
  };
  trace: {
    role: string;
    customData: string;
    query: string;
    queryHash: string;
    queryMs: number;
    llmMs: number;
    resultRowCount: number;
    inputTokens: number;
    outputTokens: number;
    model: string;
    metadataMode: 'live-fabric-iq-schema';
    schemaHash: string;
    generationModel: string;
    generationInputTokens: number;
    generationOutputTokens: number;
    generationMs: number;
  };
}

export const executionSteps = [
  'identity', 'iq-auth', 'iq-initialize', 'iq-tools', 'iq-schema',
  'dax-generation', 'rls-query', 'explanation', 'complete',
] as const;

export const multiOperationSteps = new Set<string>(['iq-initialize', 'iq-tools']);

export interface ExecutionEvent {
  sequence: number;
  requestId: string;
  step: typeof executionSteps[number] | 'request';
  status: 'started' | 'completed' | 'failed' | 'cancelled';
  operation: string;
  identity: string;
  timestampUtc: string;
  elapsedMs: number;
  details: Record<string, string>;
}

export const streamLimits = {
  totalBytes: 1024 * 1024,
  lineBytes: 256 * 1024,
  events: 256,
} as const;

export class DemoApiError extends Error {
  constructor(
    message: string,
    readonly code: string,
    readonly status: number | null = null,
    readonly requestId: string | null = null,
  ) {
    super(message);
    this.name = 'DemoApiError';
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isNullableString(value: unknown): value is string | null {
  return value === null || typeof value === 'string';
}

function isNonNegativeNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0;
}

function isNonNegativeInteger(value: unknown): value is number {
  return isNonNegativeNumber(value) && Number.isSafeInteger(value);
}

function isNonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value.trim().length > 0;
}

function isIdentity(value: unknown): value is DemoIdentity {
  return isRecord(value) && typeof value.id === 'string' && value.id.length > 0
    && typeof value.label === 'string' && typeof value.scope === 'string';
}

function isQuestion(value: unknown): value is PreparedQuestion {
  return isRecord(value) && typeof value.id === 'string' && value.id.length > 0
    && typeof value.text === 'string' && typeof value.purpose === 'string';
}

function hasUniqueIds(items: { id: string }[]): boolean {
  return new Set(items.map((item) => item.id)).size === items.length;
}

function isCatalog(value: unknown): value is DemoCatalog {
  return isRecord(value)
    && value.mode === 'technical-proof'
    && value.executionMode === 'fabric-iq-generated-dax'
    && typeof value.liveEnabled === 'boolean'
    && isNullableString(value.liveUntilUtc)
    && (value.liveUntilUtc === null || Number.isFinite(Date.parse(value.liveUntilUtc)))
    && isNullableString(value.llmModel)
    && typeof value.identityNotice === 'string'
    && typeof value.evidenceNote === 'string'
    && Array.isArray(value.identities) && value.identities.every(isIdentity)
    && hasUniqueIds(value.identities)
    && Array.isArray(value.questions) && value.questions.every(isQuestion)
    && hasUniqueIds(value.questions);
}

function isColumn(value: unknown): value is ResultColumn {
  return isRecord(value) && typeof value.name === 'string'
    && typeof value.arrowType === 'string' && typeof value.nullable === 'boolean';
}

function isAskResult(value: unknown): value is AskResult {
  if (!isRecord(value) || !isRecord(value.data) || !isRecord(value.trace)) return false;
  const { data, trace } = value;
  if (!Array.isArray(data.columns) || !data.columns.every(isColumn)) return false;
  const columnCount = data.columns.length;
  if (!Array.isArray(data.rows) || !data.rows.every((row: unknown) =>
    Array.isArray(row) && row.length === columnCount && row.every(isNullableString))) return false;

  return isNonEmptyString(value.requestId)
    && isNonEmptyString(value.userId)
    && isNonEmptyString(value.questionId)
    && typeof value.question === 'string'
    && typeof value.answer === 'string'
    && value.answerSource === 'llm'
    && isNonEmptyString(trace.role)
    && isNonEmptyString(trace.customData)
    && isNonEmptyString(trace.query)
    && isNonEmptyString(trace.queryHash)
    && isNonNegativeNumber(trace.queryMs)
    && isNonNegativeNumber(trace.llmMs)
    && isNonNegativeInteger(trace.resultRowCount)
    && trace.resultRowCount === data.rows.length
    && isNonNegativeInteger(trace.inputTokens)
    && isNonNegativeInteger(trace.outputTokens)
    && isNonEmptyString(trace.model)
    && trace.metadataMode === 'live-fabric-iq-schema'
    && isNonEmptyString(trace.schemaHash)
    && isNonEmptyString(trace.generationModel)
    && isNonNegativeInteger(trace.generationInputTokens)
    && isNonNegativeInteger(trace.generationOutputTokens)
    && isNonNegativeNumber(trace.generationMs);
}

function isExecutionEvent(value: unknown): value is ExecutionEvent {
  return isRecord(value)
    && isNonNegativeInteger(value.sequence)
    && isNonEmptyString(value.requestId)
    && (value.step === 'request' || executionSteps.some(step => step === value.step))
    && typeof value.status === 'string'
    && ['started', 'completed', 'failed', 'cancelled'].includes(value.status)
    && (value.step !== 'request' || value.status === 'failed' || value.status === 'cancelled')
    && isNonEmptyString(value.operation)
    && isNonEmptyString(value.identity)
    && typeof value.timestampUtc === 'string'
    && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|\+00:00)$/.test(value.timestampUtc)
    && Number.isFinite(Date.parse(value.timestampUtc))
    && isNonNegativeNumber(value.elapsedMs)
    && isRecord(value.details)
    && Object.entries(value.details).every(([key, detail]) =>
      key.length > 0 && typeof detail === 'string');
}

function malformedResponse(requestId: string | null = null, reason = 'The response did not match the API contract.'): DemoApiError {
  return new DemoApiError(
    `The local backend returned an unexpected response. ${reason} No results were displayed. Received diagnostics are retained for this selection.`,
    'invalid_response',
    null,
    requestId,
  );
}

async function request(path: string, init: RequestInit): Promise<Response> {
  try {
    return await fetch(path, { ...init, credentials: 'omit', cache: 'no-store' });
  } catch (error: unknown) {
    if (init.signal?.aborted) throw error;
    throw new DemoApiError(
      'Cannot reach the local backend. Start the demo host and open this app at http://127.0.0.1:5187, then retry.',
      'backend_unavailable',
    );
  }
}

async function readJson(response: Response): Promise<unknown> {
  let body: unknown;
  try {
    body = await response.json();
  } catch {
    throw new DemoApiError(
      `The local backend returned HTTP ${response.status} without the expected JSON response. No fallback data is available.`,
      'invalid_response',
      response.status,
    );
  }

  if (!response.ok) {
    if (isRecord(body) && typeof body.error === 'string' && typeof body.message === 'string') {
      throw new DemoApiError(
        body.message, body.error, response.status,
        typeof body.requestId === 'string' ? body.requestId : null,
      );
    }
    throw new DemoApiError(
      `The request failed (HTTP ${response.status}). No answer or results were substituted.`,
      'request_failed',
      response.status,
    );
  }
  return body;
}

export async function loadCatalog(signal: AbortSignal): Promise<DemoCatalog> {
  const response = await request('/api/demo', {
    method: 'GET',
    headers: { Accept: 'application/json' },
    signal,
  });
  const value = await readJson(response);
  if (!isCatalog(value)) throw malformedResponse();
  return value;
}

export async function askQuestion(
  userId: string,
  questionId: string,
  signal: AbortSignal,
  onEvent?: (event: ExecutionEvent) => void,
): Promise<AskResult> {
  const response = await request('/api/ask', {
    method: 'POST',
    headers: { Accept: 'application/x-ndjson', 'Content-Type': 'application/json', 'X-Demo-Request': '1' },
    body: JSON.stringify({ userId, questionId }),
    signal,
  });
  if (!response.ok) {
    await readJson(response);
    throw malformedResponse();
  }
  if (response.headers.get('Content-Type')?.split(';')[0].trim().toLowerCase() !== 'application/x-ndjson'
    || !response.body) {
    await response.body?.cancel();
    throw malformedResponse(null, 'Expected an NDJSON execution stream, not a buffered answer.');
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder('utf-8', { fatal: true });
  let requestId: string | null = null;
  let lastSequence = -1;
  let eventCount = 0;
  let totalBytes = 0;
  let lineBytes = 0;
  let pending = '';
  let result: AskResult | null = null;
  let terminalError: DemoApiError | null = null;
  let complete = false;
  let failed = false;
  let reachedEof = false;
  const stages = new Map<ExecutionEvent['step'], ExecutionEvent['status']>();

  const abortReader = () => { void reader.cancel().catch(() => undefined); };
  signal.addEventListener('abort', abortReader, { once: true });

  function checkRequestId(value: string): void {
    if (requestId !== null && requestId !== value) {
      throw malformedResponse(requestId, 'The stream changed request ID.');
    }
    requestId = value;
  }

  function consumeLine(line: string): void {
    if (result || terminalError) throw malformedResponse(requestId, 'A frame arrived after the terminal frame.');
    let frame: unknown;
    try {
      frame = JSON.parse(line);
    } catch {
      throw malformedResponse(requestId, 'A stream line was not valid JSON.');
    }
    if (!isRecord(frame)) throw malformedResponse(requestId);
    if (frame.type === 'event') {
      if (!isExecutionEvent(frame.event)) throw malformedResponse(requestId, 'An execution event was malformed.');
      const event = frame.event;
      checkRequestId(event.requestId);
      if ((complete && event.step !== 'request') || event.sequence <= lastSequence || ++eventCount > streamLimits.events) {
        throw malformedResponse(requestId, 'Execution events were out of order or exceeded the event limit.');
      }
      const previous = stages.get(event.step);
      const nextOperation = previous === 'completed' && event.status === 'started';
      if (previous && previous !== 'started' && !nextOperation) {
        throw malformedResponse(requestId, 'An already finished stage emitted another event.');
      }
      if (previous === 'started' && event.status === 'started') {
        throw malformedResponse(requestId, 'A stage emitted duplicate start events.');
      }
      if (event.step === 'complete' && event.status === 'completed') {
        if (failed || executionSteps.slice(0, -1).some(step => stages.get(step) !== 'completed')) {
          throw malformedResponse(requestId, 'Completion was reported while stages were unconfirmed or failed.');
        }
        complete = true;
      }
      failed ||= event.status === 'failed' || event.status === 'cancelled';
      stages.set(event.step, event.status);
      lastSequence = event.sequence;
      // Copy only the event contract; never expose unrecognized response fields or headers.
      onEvent?.({
        sequence: event.sequence, requestId: event.requestId, step: event.step,
        status: event.status, operation: event.operation, identity: event.identity,
        timestampUtc: event.timestampUtc, elapsedMs: event.elapsedMs,
        details: { ...event.details },
      });
    } else if (frame.type === 'result') {
      if (!isAskResult(frame.result)) throw malformedResponse(requestId, 'The result was malformed.');
      const value = frame.result;
      checkRequestId(value.requestId);
      if (value.userId !== userId || value.questionId !== questionId || value.trace.customData !== userId) {
        throw new DemoApiError(
          'The response does not match the selected user and question. It was discarded; no results were displayed.',
          'response_mismatch', null, requestId,
        );
      }
      if (!complete || failed) throw malformedResponse(requestId, 'A result arrived without confirmed execution completion.');
      result = value;
    } else if (frame.type === 'error') {
      if (!isNonEmptyString(frame.error) || !isNonEmptyString(frame.message) || !isNonEmptyString(frame.requestId)) {
        throw malformedResponse(requestId, 'The error frame was malformed.');
      }
      checkRequestId(frame.requestId);
      terminalError = new DemoApiError(frame.message, frame.error, response.status, requestId);
    } else {
      throw malformedResponse(requestId, 'An unknown stream frame was received.');
    }
  }

  try {
    signal.throwIfAborted();
    while (true) {
      const { value, done } = await reader.read();
      signal.throwIfAborted();
      if (done) {
        reachedEof = true;
        pending += decoder.decode();
        if (pending.length > 0) consumeLine(pending);
        break;
      }
      totalBytes += value.byteLength;
      if (totalBytes > streamLimits.totalBytes) throw malformedResponse(requestId, 'The stream exceeded the total byte limit.');
      for (const byte of value) {
        if (byte === 10) lineBytes = 0;
        else if (++lineBytes > streamLimits.lineBytes) throw malformedResponse(requestId, 'A stream line exceeded the byte limit.');
      }
      pending += decoder.decode(value, { stream: true });
      let newline: number;
      while ((newline = pending.indexOf('\n')) !== -1) {
        const line = pending.slice(0, newline);
        pending = pending.slice(newline + 1);
        consumeLine(line);
      }
    }
    if (terminalError) throw terminalError;
    if (!result) throw malformedResponse(requestId, 'The stream ended without a result.');
    return result;
  } catch (error: unknown) {
    if (signal.aborted || error instanceof DemoApiError) throw error;
    throw malformedResponse(requestId, 'The stream was interrupted or contained invalid UTF-8.');
  } finally {
    signal.removeEventListener('abort', abortReader);
    if (!reachedEof) await reader.cancel().catch(() => undefined);
    reader.releaseLock();
  }
}

export function readableError(error: unknown): DemoApiError {
  return error instanceof DemoApiError
    ? error
    : new DemoApiError('The request could not be completed. No answer or results were substituted.', 'request_failed');
}
