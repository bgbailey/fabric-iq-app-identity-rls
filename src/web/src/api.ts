export interface AppConfig {
  model: string;
  identityMode: 'CustomData' | 'EffectiveUsername';
  rlsRole: string;
  embedEnabled: boolean;
  auth: { mode: 'Development' | 'Oidc'; users: { username: string; displayName: string }[] | null };
  suggestions: string[];
}

export interface TokenResponse {
  access_token: string;
  token_type: 'Bearer';
  expires_in: number;
}

export interface MeResponse {
  subject: string;
  displayName: string;
  userKey: string;
  identityMode: string;
  role: string;
}

export interface ChatMessage {
  role: 'user' | 'assistant';
  content: string;
}

export type StepName = 'authenticate' | 'model' | 'tool' | 'answer';

export interface ChatEvent {
  step: StepName;
  status: 'started' | 'completed' | 'failed';
  name: string;
  elapsedMs: number;
  details: Record<string, string>;
}

export interface ResultTable {
  columns: { name: string; type: string }[];
  rows: (string | null)[][];
}

export interface ToolCall {
  name: string;
  arguments: string;
  dax: string | null;
  rowCount: number | null;
  isError: boolean;
}

export interface ChatResult {
  answer: string;
  toolCalls: ToolCall[];
  table: ResultTable | null;
  usage: { model: string; modelCalls: number; inputTokens: number; outputTokens: number };
}

export interface EmbedConfig {
  reportId: string;
  embedUrl: string;
  token: string;
  expiration: string;
}

export type ResultSet = ResultTable;

export class ApiError extends Error {
  constructor(message: string, readonly status?: number, readonly code?: string) {
    super(message);
    this.name = 'ApiError';
  }
}

async function parseError(response: Response): Promise<ApiError> {
  try {
    const body = await response.json() as { error?: string; message?: string; error_description?: string };
    return new ApiError(body.message ?? body.error_description ?? body.error ?? `HTTP ${response.status}`, response.status, body.error);
  } catch {
    return new ApiError(`HTTP ${response.status}`, response.status);
  }
}

async function json<T>(path: string, init: RequestInit = {}): Promise<T> {
  const response = await fetch(path, { ...init, credentials: 'omit', cache: 'no-store' });
  if (!response.ok) throw await parseError(response);
  return await response.json() as T;
}

function auth(token: string): HeadersInit {
  return { Authorization: `Bearer ${token}` };
}

export function loadConfig(signal?: AbortSignal): Promise<AppConfig> {
  return json<AppConfig>('/api/config', { signal });
}

export function signIn(username: string, password: string, signal?: AbortSignal): Promise<TokenResponse> {
  return json<TokenResponse>('/dev-idp/token', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, password }),
    signal,
  });
}

export function loadMe(token: string, signal?: AbortSignal): Promise<MeResponse> {
  return json<MeResponse>('/api/me', { headers: auth(token), signal });
}

export function loadEmbed(token: string, signal?: AbortSignal): Promise<EmbedConfig> {
  return json<EmbedConfig>('/api/embed', { headers: auth(token), signal });
}

export function loadParityRows(token: string, signal?: AbortSignal): Promise<ResultTable> {
  return json<ResultTable>('/api/parity', { headers: auth(token), signal });
}

export async function readChatStream(response: Response, onEvent: (event: ChatEvent) => void): Promise<ChatResult> {
  if (!response.ok) throw await parseError(response);
  if (!response.body) throw new ApiError('The chat response did not include a stream.');

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';

  while (true) {
    const { value, done } = await reader.read();
    buffer += done ? decoder.decode() : decoder.decode(value, { stream: true });
    const lines = buffer.split('\n');
    buffer = done ? '' : lines.pop() ?? '';

    for (const line of lines) {
      if (!line.trim()) continue;
      const frame = JSON.parse(line) as { type: string; event?: ChatEvent; result?: ChatResult; message?: string };
      if (frame.type === 'event' && frame.event) onEvent(frame.event);
      if (frame.type === 'result' && frame.result) return frame.result;
      if (frame.type === 'error') throw new ApiError(frame.message ?? 'Chat failed.');
    }

    if (done) break;
  }

  throw new ApiError('The chat stream ended without a result.');
}

export async function sendChat(
  token: string,
  message: string,
  history: ChatMessage[],
  signal: AbortSignal,
  onEvent: (event: ChatEvent) => void,
): Promise<ChatResult> {
  const response = await fetch('/api/chat', {
    method: 'POST',
    headers: { ...auth(token), 'Content-Type': 'application/json' },
    body: JSON.stringify({ message, history: history.slice(-6) }),
    signal,
    credentials: 'omit',
    cache: 'no-store',
  });
  return readChatStream(response, onEvent);
}

export function readableError(error: unknown): ApiError {
  return error instanceof ApiError ? error : new ApiError(error instanceof Error ? error.message : 'Request failed.');
}
