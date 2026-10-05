import type { ChatEvent } from './api';

const steps: ChatEvent['step'][] = ['authenticate', 'model', 'tool', 'answer'];
const labels: Record<ChatEvent['step'], string> = {
  authenticate: 'Authenticate',
  model: 'Model',
  tool: 'Tool call',
  answer: 'Answer',
};

export function ExecutionTrace({ events }: { events: ChatEvent[] }) {
  return (
    <details className="execution-panel" aria-labelledby="execution-heading">
      <summary className="section-heading">
        <h3 id="execution-heading">Gateway steps</h3>
        <span className="small-label">{events.some(event => event.status === 'failed') ? 'A step failed - expand details' : 'Expand technical details'}</span>
      </summary>
      <ol className="execution-timeline">
        {steps.map(step => {
          const stepEvents = events.filter(event => event.step === step);
          const latest = stepEvents.at(-1);
          return (
            <li key={step} className={`execution-stage stage-${latest?.status ?? 'not-run'}`}>
              <div className="stage-title">
                <h4>{labels[step]}</h4>
                <span className="stage-status">{latest?.status ?? 'not run'}</span>
              </div>
              {stepEvents.length === 0 ? <p className="stage-pending">No event yet.</p> : stepEvents.map((event, index) => (
                <div className="event-detail" key={`${event.step}-${index}`}>
                  <p className="stage-operation"><code>{event.name}</code> · {event.elapsedMs} ms</p>
                  {event.details.dax && <pre tabIndex={0}><code>{event.details.dax}</code></pre>}
                  {Object.keys(event.details).length > 0 && (
                    <dl className="detail-list">
                      {Object.entries(event.details).filter(([key]) => key !== 'dax').map(([key, value]) => (
                        <div key={key} className={key === 'arguments' ? 'detail-full' : undefined}>
                          <dt>{key}</dt>
                          <dd><code>{value}</code></dd>
                        </div>
                      ))}
                    </dl>
                  )}
                </div>
              ))}
            </li>
          );
        })}
      </ol>
    </details>
  );
}
