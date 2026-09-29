import type { ChatResult } from './api';

export function Results({ result }: { result: ChatResult }) {
  const table = result.table;
  return (
    <div className="response-stack">
      <section className="explanation" aria-labelledby="answer-heading">
        <div className="section-heading">
          <h3 id="answer-heading">Answer</h3>
          <span className="small-label">{result.usage.model}</span>
        </div>
        <p className="answer-text">{result.answer}</p>
      </section>

      <section className="results-section" aria-labelledby="results-heading">
        <div className="section-heading">
          <h3 id="results-heading">Result table</h3>
          <span className="row-count">{table?.rows.length ?? 0} rows</span>
        </div>
        {table ? (
          <div className="table-scroll" role="region" aria-label="Result table" tabIndex={0}>
            <table>
              <thead>
                <tr>{table.columns.map(column => <th key={column.name}>{column.name}<span className="column-type">{column.type}</span></th>)}</tr>
              </thead>
              <tbody>
                {table.rows.map((row, rowIndex) => (
                  <tr key={rowIndex}>{row.map((cell, cellIndex) => <td key={cellIndex}>{cell ?? <span className="null-value">NULL</span>}</td>)}</tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : <p className="empty-rows">No table returned.</p>}
      </section>
    </div>
  );
}
