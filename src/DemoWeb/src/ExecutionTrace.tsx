import { useEffect, useState } from 'react';
import { executionSteps, multiOperationSteps } from './api';
import type { DemoApiError, ExecutionEvent } from './api';

const stepLabels: Record<ExecutionEvent['step'], string> = {
  identity: 'Resolve application identity',
  'iq-auth': 'Acquire delegated IQ access',
  'iq-initialize': 'Initialize Fabric IQ',
  'iq-tools': 'Discover IQ tools',
  'iq-schema': 'Read live schema input',
  'dax-generation': 'Generate DAX with the LLM',
  'rls-query': 'Execute through authenticated broker + engine RLS',
  explanation: 'Explain filtered rows with the LLM',
  complete: 'Complete execution',
  request: 'Request-level diagnostic',
};

interface ExecutionTraceProps {
  events: ExecutionEvent[];
  state: 'idle' | 'running' | 'success' | 'error' | 'cancelled';
  error?: DemoApiError;
}

function eventLog(events: ExecutionEvent[], state: ExecutionTraceProps['state'], error?: DemoApiError): string {
  const lines = events.map(event => JSON.stringify(event));
  if (error) lines.push(JSON.stringify({
    diagnostic: 'client-observed-error', code: error.code, message: error.message,
    requestId: error.requestId, httpStatus: error.status,
  }));
  if (state === 'cancelled') lines.push('BROWSER CANCELLED: observation stopped; server completion is unconfirmed.');
  return lines.join('\n') || 'No backend execution events received. All planned stages are NOT RUN.';
}

export function ExecutionTrace({ events, state, error }: ExecutionTraceProps) {
  const [copyStatus, setCopyStatus] = useState('');
  const log = eventLog(events, state, error);
  const latest = events.at(-1);
  const requestId = latest?.requestId ?? error?.requestId;
  const incomplete = state === 'error' || state === 'cancelled';
  const requestEvents = events.filter(event => event.step === 'request');
  const completeObserved = events.some(event => event.step === 'complete' && event.status === 'completed');

  useEffect(() => setCopyStatus(''), [log]);

  async function copyLog(): Promise<void> {
    try {
      await navigator.clipboard.writeText(log);
      setCopyStatus('Event log copied.');
    } catch {
      setCopyStatus('Clipboard unavailable. Select and copy the complete log below.');
    }
  }

  return (
    <section className="execution-panel" aria-labelledby="execution-heading">
      <div className="section-heading">
        <h3 id="execution-heading">Live execution</h3>
        <span className="small-label">Backend events only · No simulated progress</span>
      </div>
      <p className="execution-status" role="status" aria-live="polite" aria-atomic="true">
        {state === 'running'
          ? latest ? `Latest event: ${stepLabels[latest.step]} — ${latest.status}.`
            : 'Request sent. Waiting for the first backend event.'
          : state === 'success' ? 'Result validated; execution stream closed.'
            : state === 'error' ? 'Request failed. Received diagnostics remain below; unconfirmed work is not marked complete.'
              : state === 'cancelled' ? 'Browser cancelled. Received diagnostics retained; backend completion is unconfirmed.'
                : 'Not running. Choose a question, then run explicitly.'}
      </p>
      <p className="execution-request">Request ID: {requestId ? <code>{requestId}</code> : <span>Not received</span>}</p>
      <ol className="execution-timeline" aria-label="Backend execution stages">
        {executionSteps.map(step => {
          const stageEvents = events.filter(event => event.step === step);
          const event = stageEvents.at(-1);
          const unconfirmed = incomplete && event?.status === 'started';
          const operationOnly = !completeObserved && event?.status === 'completed' && multiOperationSteps.has(step);
          return (
            <li key={step} className={`execution-stage stage-${operationOnly ? 'operation-completed' : event?.status ?? 'not-run'}`} data-step={step}>
              <div className="stage-title">
                <h4>{stepLabels[step]}</h4>
                <span className="stage-status">{event ? unconfirmed ? 'STARTED · UNCONFIRMED' : operationOnly ? 'OPERATION COMPLETED · STAGE UNCONFIRMED' : event.status.toUpperCase() : 'NOT RUN'}</span>
              </div>
              {event ? (
                <>
                  <p className="stage-operation"><code>{event.operation}</code></p>
                  <p className="stage-meta"><span>Identity: <code>{event.identity}</code></span><span>elapsedMs: <code>{event.elapsedMs}</code> · #{event.sequence}</span></p>
                  {multiOperationSteps.has(step) && <p className="stage-pending">{stageEvents.filter(item => item.status === 'completed').length} completed operation(s). {step === 'iq-initialize' ? 'Initialization includes the initialized notification.' : 'Tool discovery may include multiple pages.'} Stage completion is confirmed by the backend completion event, not by the first operation.</p>}
                  <details className="event-details">
                    <summary>Event details ({stageEvents.length}){step === 'iq-schema' ? ' · schema input' : step === 'dax-generation' ? ' · generated DAX' : ''}</summary>
                    {stageEvents.map(item => (
                      <div className="event-detail" key={item.sequence}>
                        <p className="small-label">#{item.sequence} · {item.status} · {item.timestampUtc} · elapsedMs: {item.elapsedMs}</p>
                        <p className="small-label">Request <code>{item.requestId}</code> · Identity <code>{item.identity}</code></p>
                        <pre tabIndex={0} aria-label={`${step} event ${item.sequence}`}><code>{JSON.stringify({
                          operation: item.operation, details: item.details,
                        }, null, 2)}</code></pre>
                      </div>
                    ))}
                  </details>
                </>
              ) : <p className="stage-pending">No backend event received. This stage has not been observed running.</p>}
            </li>
          );
        })}
      </ol>
      {requestEvents.map(event => (
        <section className={`execution-stage stage-${event.status} request-diagnostic`} key={event.sequence} aria-label="Request-level diagnostic">
          <div className="stage-title"><h4>Request-level diagnostic</h4><span className="stage-status">{event.status.toUpperCase()}</span></div>
          <p className="stage-operation"><code>{event.operation}</code></p>
          <p className="stage-meta"><span>Identity: <code>{event.identity}</code></span><span>elapsedMs: <code>{event.elapsedMs}</code> · #{event.sequence}</span></p>
          <p className="small-label">This request-level event does not change previously completed stage statuses.</p>
          <details className="event-details">
            <summary>Request event details</summary>
            <pre tabIndex={0} aria-label={`request event ${event.sequence}`}><code>{JSON.stringify(event, null, 2)}</code></pre>
          </details>
        </section>
      ))}
      <details className="technical-trace execution-log">
        <summary><span>Complete event log</span><span className="small-label">Plain text · Current selection only</span></summary>
        <div className="trace-body">
          <p className="log-warning">Copy only when appropriate: this log includes synthetic app-user context, live schema and generated DAX when returned. Only backend-allowlisted diagnostics are shown; credentials and tokens are not requested or included.</p>
          <button className="secondary-button" disabled={!events.length && !error} onClick={() => void copyLog()}>Copy complete event log</button>
          <p className="small-label" role="status">{copyStatus}</p>
          <pre className="terminal-log" tabIndex={0} aria-label="Complete plaintext event log"><code>{log}</code></pre>
        </div>
      </details>
    </section>
  );
}
