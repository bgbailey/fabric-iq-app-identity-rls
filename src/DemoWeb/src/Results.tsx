import type { AskResult, DemoIdentity } from './api';

export function Results({ result, identity }: { result: AskResult; identity: DemoIdentity }) {
  return (
    <div className="response-stack">
      <section className="explanation" aria-labelledby="explanation-heading">
        <div className="section-heading">
          <h3 id="explanation-heading">LLM explanation</h3>
          <span className="small-label">Model: {result.trace.model}</span>
        </div>
        <p className="answer-text">{result.answer}</p>
        <p className="explanation-note">Generated explanation of this result set. Use the model-returned rows below as the source of truth.</p>
      </section>

      <section className="results-section" aria-labelledby="results-heading">
        <div className="section-heading">
          <h3 id="results-heading">Semantic model results</h3>
          <span className="row-count">{result.trace.resultRowCount} {result.trace.resultRowCount === 1 ? 'row' : 'rows'}</span>
        </div>
        <p className="small-label result-scope">Returned for {identity.label} · Scope: {identity.scope}</p>
        {result.data.columns.length > 0 ? (
          <div className="table-scroll" role="region" aria-label="Semantic model results table" tabIndex={0}>
            <table>
              <caption className="sr-only">Semantic model results for {identity.label}. Values are shown exactly as returned; NULL means a blank model value.</caption>
              <thead>
                <tr>
                  {result.data.columns.map((column, index) => (
                    <th key={`${index}-${column.name}`} scope="col">
                      <span>{column.name}</span>
                      <span className="column-type">{column.arrowType}{column.nullable ? ' · nullable' : ''}</span>
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {result.data.rows.map((row, rowIndex) => (
                  <tr key={rowIndex}>
                    {row.map((cell, columnIndex) => (
                      <td key={columnIndex}>
                        {cell === null
                          ? <span className="null-value" aria-label="Null value">NULL</span>
                          : cell === ''
                            ? <span className="null-value" aria-label="Empty string">EMPTY STRING</span>
                            : cell}
                      </td>
                    ))}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}
        {result.data.rows.length === 0 && (
          <p className="empty-rows">The semantic model returned no rows for this user and question. An empty result is not an error.</p>
        )}
        <p className="table-note">Exact backend values; decimal precision is preserved. No browser-side row filtering.</p>
      </section>

      <details className="technical-trace" open>
        <summary>
          <span>Technical trace</span>
          <span className="small-label">Identity, generated DAX, timings &amp; provenance</span>
        </summary>
        <div className="trace-body">
          <p className="provenance">Prepared question → live Fabric IQ schema → LLM-generated DAX → authenticated broker → engine RLS → LLM explanation</p>
          <dl className="trace-grid">
            <div><dt>Request ID</dt><dd><code>{result.requestId}</code></dd></div>
            <div><dt>Question ID</dt><dd><code>{result.questionId}</code></dd></div>
            <div><dt>Role</dt><dd><code>{result.trace.role}</code></dd></div>
            <div><dt>CustomData</dt><dd><code>{result.trace.customData}</code></dd></div>
            <div><dt>Query duration</dt><dd>{result.trace.queryMs} ms</dd></div>
            <div><dt>Explanation duration</dt><dd>{result.trace.llmMs} ms</dd></div>
            <div><dt>IQ + DAX planning duration</dt><dd>{result.trace.generationMs} ms</dd></div>
            <div><dt>DAX generation model</dt><dd><code>{result.trace.generationModel}</code></dd></div>
            <div><dt>Generation input tokens</dt><dd>{result.trace.generationInputTokens}</dd></div>
            <div><dt>Generation output tokens</dt><dd>{result.trace.generationOutputTokens}</dd></div>
            <div><dt>Returned rows</dt><dd>{result.trace.resultRowCount}</dd></div>
            <div><dt>Explanation model</dt><dd><code>{result.trace.model}</code></dd></div>
            <div><dt>Explanation input tokens</dt><dd>{result.trace.inputTokens}</dd></div>
            <div><dt>Explanation output tokens</dt><dd>{result.trace.outputTokens}</dd></div>
            <div className="trace-full"><dt>Query hash</dt><dd><code>{result.trace.queryHash}</code></dd></div>
            <div className="trace-full"><dt>Schema hash</dt><dd><code>{result.trace.schemaHash}</code></dd></div>
            <div className="trace-full"><dt>Metadata mode</dt><dd><code>{result.trace.metadataMode}</code></dd></div>
          </dl>
          <div className="query-heading"><h4>Generated DAX</h4><span className="small-label">Actual query for this request; may differ across users or runs</span></div>
          <pre tabIndex={0} aria-label="Generated DAX query"><code>{result.trace.query}</code></pre>
          <p className="small-label">The browser sends only catalog user and question IDs. Live schema input is in the IQ schema event details above. The authenticated broker—not the LLM or browser—selects role and CustomData.</p>
        </div>
      </details>
    </div>
  );
}
