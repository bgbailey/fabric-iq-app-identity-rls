import { useCallback, useEffect, useRef, useState } from 'react';
import { askQuestion, loadCatalog, readableError } from './api';
import type { AskResult, DemoApiError, DemoCatalog, ExecutionEvent } from './api';
import { availability } from './availability';
import { ExecutionTrace } from './ExecutionTrace';
import { RequestGate } from './requestGate';
import { Results } from './Results';

type QueryState =
  | { kind: 'idle'; notice: string }
  | { kind: 'running' }
  | { kind: 'cancelled'; notice: string }
  | { kind: 'success'; result: AskResult }
  | { kind: 'error'; error: DemoApiError };

const initialNotice = 'Select a prepared question, then run it for the selected demo user.';

function ErrorMessage({ error }: { error: DemoApiError }) {
  return (
    <div className="error-message" role="alert">
      <h3>Request not completed</h3>
      <p>{error.message}</p>
      <div className="error-meta">
        <code>{error.code}</code>
        {error.status !== null && <span>HTTP {error.status}</span>}
        {error.requestId && <span>Request <code>{error.requestId}</code></span>}
      </div>
      <p className="small-label">No fallback answer or substitute results are shown. A request may already have used the backend budget; retry only when ready.</p>
    </div>
  );
}

export function App() {
  const [catalog, setCatalog] = useState<DemoCatalog | null>(null);
  const [catalogLoading, setCatalogLoading] = useState(true);
  const [catalogError, setCatalogError] = useState<DemoApiError | null>(null);
  const [selectedUserId, setSelectedUserId] = useState('');
  const [selectedQuestionId, setSelectedQuestionId] = useState('');
  const [query, setQuery] = useState<QueryState>({ kind: 'idle', notice: initialNotice });
  const [events, setEvents] = useState<ExecutionEvent[]>([]);
  const [now, setNow] = useState(Date.now);
  const catalogGate = useRef(new RequestGate());
  const queryGate = useRef(new RequestGate());

  const refreshCatalog = useCallback(async () => {
    queryGate.current.invalidate();
    setQuery({ kind: 'idle', notice: initialNotice });
    setEvents([]);
    const ticket = catalogGate.current.begin();
    setCatalogLoading(true);
    setCatalogError(null);
    setCatalog(null);
    try {
      const next = await loadCatalog(ticket.signal);
      if (!ticket.isCurrent()) return;
      setCatalog(next);
      setSelectedUserId((previous) => next.identities.some((identity) => identity.id === previous)
        ? previous : (next.identities[0]?.id ?? ''));
      setSelectedQuestionId((previous) => next.questions.some((question) => question.id === previous)
        ? previous : (next.questions[0]?.id ?? ''));
      setNow(Date.now());
    } catch (error: unknown) {
      if (ticket.isCurrent()) setCatalogError(readableError(error));
    } finally {
      if (ticket.isCurrent()) setCatalogLoading(false);
    }
  }, []);

  useEffect(() => {
    void refreshCatalog();
    const catalogRequests = catalogGate.current;
    const queryRequests = queryGate.current;
    return () => {
      catalogRequests.invalidate();
      queryRequests.invalidate();
    };
  }, [refreshCatalog]);

  useEffect(() => {
    if (!catalog?.liveEnabled || !catalog.liveUntilUtc) return;
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [catalog]);

  const status = availability(catalog, now);
  const identity = catalog?.identities.find((item) => item.id === selectedUserId);
  const question = catalog?.questions.find((item) => item.id === selectedQuestionId);
  const running = query.kind === 'running';

  function selectUser(userId: string): void {
    if (userId === selectedUserId) return;
    queryGate.current.invalidate();
    setEvents([]);
    setSelectedUserId(userId);
    setQuery({
      kind: 'idle',
      notice: 'Demo user changed. Previous events, schema, DAX, answer and results were cleared. Run the same question to compare the new scope.',
    });
  }

  function selectQuestion(questionId: string): void {
    if (questionId === selectedQuestionId) return;
    queryGate.current.invalidate();
    setEvents([]);
    setSelectedQuestionId(questionId);
    setQuery({ kind: 'idle', notice: 'Question changed. Previous events, schema, DAX, answer and results were cleared; run when ready.' });
  }

  function cancelQuery(): void {
    queryGate.current.invalidate();
    setQuery({
      kind: 'cancelled',
      notice: 'Request cancelled in this browser. Received diagnostics remain for this selection; late events and results are discarded. Work already started by the backend may still count toward its budget.',
    });
  }

  async function runQuestion(): Promise<void> {
    const currentAvailability = availability(catalog, Date.now());
    if (!currentAvailability.enabled || !identity || !question || running) {
      setNow(Date.now());
      return;
    }
    const ticket = queryGate.current.begin();
    setEvents([]);
    setQuery({ kind: 'running' });
    try {
      const result = await askQuestion(identity.id, question.id, ticket.signal, event => {
        if (ticket.isCurrent()) setEvents(previous => ticket.isCurrent() ? [...previous, event] : previous);
      });
      if (ticket.isCurrent()) setQuery({ kind: 'success', result });
    } catch (error: unknown) {
      if (ticket.isCurrent()) setQuery({ kind: 'error', error: readableError(error) });
    }
  }

  return (
    <div className="app-shell">
      <a className="skip-link" href="#conversation">Skip to conversation</a>
      <header className="app-header">
        <div className="brand">
          <svg className="brand-icon" viewBox="0 0 32 32" aria-hidden="true">
            <path d="M5 8h22M5 16h22M5 24h22M11 5v22M21 5v22" />
          </svg>
          <div>
            <h1>Scoped Analytics</h1>
            <p>Application identity. Model-enforced access.</p>
          </div>
        </div>
        <div className="header-meta">
          <span className="proof-badge">Technical proof</span>
          <span className={`status-badge ${status.enabled ? 'status-live' : ''}`}>
            <span className="status-dot" aria-hidden="true" />
            {catalogLoading ? 'Loading configuration' : status.label}
          </span>
        </div>
      </header>

      <div className="demo-banner">
        <strong>Synthetic data only</strong>
        <span>{catalog?.identityNotice ?? 'The user selector simulates trusted application identity. It is not a login.'}</span>
        <span className="demo-boundary">Local demo · Not production</span>
      </div>

      {catalogLoading && <div className="catalog-state" role="status"><span className="spinner" aria-hidden="true" /> Loading the local demo catalog…</div>}
      {catalogError && (
        <section className="catalog-state">
          <ErrorMessage error={catalogError} />
          <button className="primary-button" onClick={() => void refreshCatalog()}>Retry configuration</button>
        </section>
      )}

      {catalog && (
        <div className="workspace">
          <aside className="controls-panel" aria-label="Demo controls">
            <fieldset className="identity-picker">
              <legend><span className="step-label">01</span> Choose a demo user</legend>
              <p className="control-hint">Opaque application users such as app-user-A1 are not Entra-registered users. This selector is not a login.</p>
              <div className="identity-list">
                {catalog.identities.map((item) => (
                  <label key={item.id} className={`identity-option ${selectedUserId === item.id ? 'is-selected' : ''}`}>
                    <input type="radio" name="demo-user" value={item.id} checked={selectedUserId === item.id} onChange={() => selectUser(item.id)} />
                    <span>{item.label}</span>
                  </label>
                ))}
              </div>
            </fieldset>
            <div className="scope-summary" aria-label="Selected identity scope">
              <span className="eyebrow">Authorized scope</span>
              <strong>{identity?.scope ?? 'No identity available'}</strong>
              {identity && <code>{identity.id}</code>}
            </div>

            <fieldset className="question-picker">
              <legend><span className="step-label">02</span> Pick a prepared question</legend>
              <div className="question-list">
                {catalog.questions.map((item) => (
                  <label key={item.id} className={`question-option ${selectedQuestionId === item.id ? 'is-selected' : ''}`}>
                    <input type="radio" name="prepared-question" value={item.id} checked={selectedQuestionId === item.id} onChange={() => selectQuestion(item.id)} />
                    <span>
                      <span className="question-text">{item.text}</span>
                      <span className="question-purpose">{item.purpose}</span>
                    </span>
                  </label>
                ))}
              </div>
            </fieldset>
            <p className="sidebar-note">The browser selects IDs only. The semantic model—not this page—enforces row-level security (RLS).</p>
            <section className="identity-boundaries" aria-labelledby="boundaries-heading">
              <h3 id="boundaries-heading">Four separate identity contexts</h3>
              <dl>
                <div><dt>End user</dt><dd>Opaque app-user ID. Resolved by the broker; no Entra registration.</dd></div>
                <div><dt>Fabric IQ</dt><dd>Delegated IQ user credential for real schema discovery. Not the end user.</dd></div>
                <div><dt>Query broker</dt><dd>Certificate-authenticated service principal; server-selected role + CustomData reach engine RLS.</dd></div>
                <div><dt>LLM</dt><dd>Separate developer credential. Schema informs DAX generation; only filtered rows inform the explanation.</dd></div>
              </dl>
              <p className="small-label">Architecture, not run evidence. Actual operations and executing identities appear in the event stream.</p>
            </section>
          </aside>

          <main id="conversation" className="conversation-panel" tabIndex={-1}>
            <div className="conversation-header">
              <div>
                <span className="eyebrow">Scope-aware conversation</span>
                <h2>Same question. Different authorized view.</h2>
              </div>
              <p>Select another demo user and replay the question. Previous results are cleared so identities never share a conversation here.</p>
            </div>

            <div className="question-composer">
              <div className="section-heading">
                <span className="eyebrow">Prepared question</span>
                {identity && <span className="user-tag">{identity.label}</span>}
              </div>
              <p className="selected-question">{question?.text ?? 'No prepared questions are available.'}</p>
              <div className="run-row">
                <span className="run-guidance">Explicit run only · No automatic queries on selection</span>
                {running ? (
                  <button className="secondary-button" onClick={cancelQuery}>Cancel request</button>
                ) : (
                  <button className="primary-button" disabled={!status.enabled || !identity || !question} aria-describedby="availability-reason" onClick={() => void runQuestion()}>
                    Run selected question <span aria-hidden="true">→</span>
                  </button>
                )}
              </div>
            </div>

            <div className={`availability-note ${!status.enabled ? 'availability-blocked' : ''}`}>
              <p id="availability-reason">{status.reason}</p>
              <button className="text-button" onClick={() => void refreshCatalog()}>Refresh configuration</button>
            </div>

            <div className="conversation-body" aria-busy={running}>
              <div className="query-status sr-only" role="status" aria-live="polite" aria-atomic="true">
                {query.kind === 'success' ? 'Answer and semantic model results are ready.' : query.kind === 'idle' || query.kind === 'cancelled' ? query.notice : ''}
              </div>
              {(query.kind === 'idle' || query.kind === 'cancelled') && (
                <div className="empty-conversation">
                  <div className="empty-glyph" aria-hidden="true">↳</div>
                  <h3>Your selected scope, made visible</h3>
                  <p>{query.notice}</p>
                  <ol className="path-steps" aria-label="Query execution path">
                    <li><span>1</span> Prepared question</li>
                    <li><span>2</span> Live Fabric IQ schema</li>
                    <li><span>3</span> LLM-generated DAX</li>
                    <li><span>4</span> Authenticated broker → engine RLS</li>
                    <li><span>5</span> LLM explanation</li>
                  </ol>
                  <p className="empty-footnote">Only questions are prepared. DAX is generated at runtime. Diagnostics remain in memory for the current selection only; no cross-run history is stored.</p>
                </div>
              )}
              {query.kind === 'running' && (
                <div className="running-state">
                  <span className="spinner" aria-hidden="true" />
                  <h3>Request in progress</h3>
                  <p>Following actual backend operations for {identity?.label} below.</p>
                  <p className="small-label">No stage is complete until the backend reports it. You can cancel or switch users at any time.</p>
                </div>
              )}
              {query.kind === 'error' && <ErrorMessage error={query.error} />}
              <ExecutionTrace events={events} state={query.kind} error={query.kind === 'error' ? query.error : undefined} />
              {query.kind === 'success' && identity && <Results result={query.result} identity={identity} />}
            </div>

            <footer className="conversation-footer">
              <span><strong>Model</strong> {catalog.llmModel ?? 'Not configured'}</span>
              <span><strong>Live until</strong> {catalog.liveUntilUtc ?? 'Not configured'}</span>
            </footer>
          </main>
        </div>
      )}
      <footer className="page-footer">
        <p>{catalog?.evidenceNote ?? 'This local technical proof requires its backend. There are no offline result fixtures or canned explanations.'}</p>
        <span>Question → IQ schema → generated DAX → broker / RLS → explanation</span>
      </footer>
    </div>
  );
}
