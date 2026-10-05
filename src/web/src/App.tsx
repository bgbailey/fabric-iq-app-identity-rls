import { useEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { loadConfig, loadMe, readableError, sendChat, signIn } from './api';
import type { ApiError, AppConfig, ChatEvent, ChatMessage, ChatResult, MeResponse } from './api';
import { EmbeddedReport } from './EmbeddedReport';
import { ExecutionTrace } from './ExecutionTrace';
import { RequestGate } from './requestGate';
import { Results } from './Results';

type AuthState = { token: string; me: MeResponse };
type ChatState = { kind: 'idle' | 'running' | 'cancelled' } | { kind: 'done'; result: ChatResult } | { kind: 'error'; error: ApiError };

function ErrorMessage({ error }: { error: ApiError }) {
  return <div className="error-message" role="alert"><h3>Request failed</h3><p>{error.message}</p>{error.status && <p className="small-label">HTTP {error.status}</p>}</div>;
}

function SignIn({ config, onSignedIn }: { config: AppConfig; onSignedIn: (auth: AuthState) => void }) {
  const users = config.auth.users ?? [];
  const [username, setUsername] = useState(users[0]?.username ?? '');
  const [password, setPassword] = useState(config.auth.mode === 'Development' ? 'synthetic-only' : '');
  const [error, setError] = useState<ApiError | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const token = await signIn(username, password);
      const me = await loadMe(token.access_token);
      onSignedIn({ token: token.access_token, me });
    } catch (err) {
      setError(readableError(err));
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="signin-panel" aria-labelledby="signin-heading">
      <div className="section-heading"><h2 id="signin-heading">Sign in</h2><span className="small-label">{config.auth.mode}</span></div>
      <form className="signin-form" onSubmit={event => void submit(event)}>
        <label htmlFor="username">User</label>
        {users.length > 0 ? (
          <select id="username" value={username} onChange={event => setUsername(event.target.value)}>
            {users.map(user => <option key={user.username} value={user.username}>{user.displayName} ({user.username})</option>)}
          </select>
        ) : <input id="username" value={username} onChange={event => setUsername(event.target.value)} autoComplete="username" required />}
        <label htmlFor="password">Password</label>
        <input id="password" type="password" value={password} onChange={event => setPassword(event.target.value)} autoComplete="current-password" required />
        <button className="primary-button" disabled={busy}>{busy ? 'Signing in…' : 'Sign in'}</button>
      </form>
      {error && <ErrorMessage error={error} />}
    </section>
  );
}

export function App() {
  const [config, setConfig] = useState<AppConfig | null>(null);
  const [configError, setConfigError] = useState<ApiError | null>(null);
  const [auth, setAuth] = useState<AuthState | null>(null);
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [draft, setDraft] = useState('');
  const [events, setEvents] = useState<ChatEvent[]>([]);
  const [chat, setChat] = useState<ChatState>({ kind: 'idle' });
  const gate = useRef(new RequestGate());

  useEffect(() => {
    loadConfig().then(setConfig).catch(error => setConfigError(readableError(error)));
    return () => gate.current.invalidate();
  }, []);

  function clearChat() {
    gate.current.invalidate();
    setMessages([]);
    setDraft('');
    setEvents([]);
    setChat({ kind: 'idle' });
  }

  function signedIn(next: AuthState) {
    setAuth(next);
    clearChat();
  }

  function signOut() {
    setAuth(null);
    clearChat();
  }

  async function send() {
    if (!auth || !draft.trim() || chat.kind === 'running') return;
    const message = draft.trim();
    const ticket = gate.current.begin();
    const history = messages.slice(-6);
    setDraft('');
    setMessages(previous => [...previous, { role: 'user', content: message }]);
    setEvents([]);
    setChat({ kind: 'running' });
    try {
      const result = await sendChat(auth.token, message, history, ticket.signal, event => {
        if (ticket.isCurrent()) setEvents(previous => [...previous, event]);
      });
      if (!ticket.isCurrent()) return;
      setMessages(previous => [...previous, { role: 'assistant', content: result.answer }]);
      setChat({ kind: 'done', result });
    } catch (error) {
      if (ticket.isCurrent()) setChat({ kind: 'error', error: readableError(error) });
    }
  }

  return (
    <div className="app-shell">
      <header className="app-header">
        <div className="brand">
          <svg className="brand-icon" viewBox="0 0 32 32" aria-hidden="true"><path d="M5 8h22M5 16h22M5 24h22M11 5v22M21 5v22" /></svg>
          <div><h1>Contoso Insights</h1><p>AI alongside your Power BI Embedded application</p></div>
        </div>
        {config && <div className="header-meta"><span className="proof-badge">{config.model}</span><span className="proof-badge">{config.identityMode}</span></div>}
      </header>

      {configError && <ErrorMessage error={configError} />}
      {!config && !configError && <p role="status" className="catalog-state"><span className="spinner" /> Loading configuration…</p>}
      {config && !auth && <SignIn config={config} onSignedIn={signedIn} />}

      {config && auth && (
        <div className="workspace">
          <aside className="controls-panel">
            <div className="section-heading"><h2>Shared RLS identity</h2><button className="text-button" onClick={signOut}>Sign out</button></div>
            <dl className="identity-grid">
              <div><dt>Name</dt><dd>{auth.me.displayName}</dd></div>
              <div><dt>Subject</dt><dd><code>{auth.me.subject}</code></dd></div>
              <div><dt>User key</dt><dd><code>{auth.me.userKey}</code></dd></div>
              <div><dt>RLS role</dt><dd><code>{auth.me.role}</code></dd></div>
              <div><dt>Identity mode</dt><dd><code>{auth.me.identityMode}</code></dd></div>
            </dl>
          </aside>

          <main className="conversation-panel">
            {config.auth.mode === 'Development' && <p className="demo-banner">Development authentication with synthetic users. Production sign-in integration is not demonstrated here.</p>}
            {config.embedEnabled && <EmbeddedReport token={auth.token} />}
            <div className="conversation-header">
              <span className="eyebrow">Chat</span>
              <h2>Ask the semantic model as {auth.me.displayName}</h2>
              <p>Your embedded report and AI queries use the same application user key and semantic-model RLS role. Power BI enforces the granted scope in both paths.</p>
            </div>

            <section className="question-composer">
              <label className="sr-only" htmlFor="message">Message</label>
              <textarea id="message" rows={4} value={draft} onChange={event => setDraft(event.target.value)} placeholder="Ask a question…" />
              <div className="suggestion-row">
                {config.suggestions.map(suggestion => <button key={suggestion} className="chip-button" onClick={() => setDraft(suggestion)}>{suggestion}</button>)}
              </div>
              <div className="run-row">
                <span className="run-guidance">History sent: last six turns in this browser session.</span>
                {chat.kind === 'running' ? <button className="secondary-button" onClick={() => { gate.current.invalidate(); setChat({ kind: 'cancelled' }); }}>Cancel</button> : <button className="primary-button" disabled={!draft.trim()} onClick={() => void send()}>Send</button>}
              </div>
            </section>

            <div className="conversation-body">
              <section className="message-list" aria-label="Conversation">
                {messages.length === 0 && <p className="empty-rows">Start with a suggestion or type a question.</p>}
                {messages.map((message, index) => <article key={index} className={`chat-message message-${message.role}`}><strong>{message.role === 'user' ? 'You' : 'Assistant'}</strong><p>{message.content}</p></article>)}
              </section>
              {chat.kind === 'running' && <p role="status" className="running-state"><span className="spinner" /> Asking the gateway…</p>}
              {chat.kind === 'error' && <ErrorMessage error={chat.error} />}
              <ExecutionTrace events={events} />
              {chat.kind === 'done' && <Results result={chat.result} />}
            </div>
          </main>
        </div>
      )}

      <footer className="page-footer"><p>Synthetic data for learning and development.</p><span>Sign in → Embedded report → AI → RLS scope</span></footer>
    </div>
  );
}
