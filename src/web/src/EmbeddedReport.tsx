import { useRef, useState } from 'react';
import * as powerbi from 'powerbi-client';
import { loadEmbed, loadParityRows, readableError } from './api';
import type { ApiError, EmbedConfig } from './api';
import { compareParity, rowsFromCsv, rowsFromResultSet } from './parity';
import type { ParityComparison } from './parity';

const powerbiService = new powerbi.service.Service(
  powerbi.factories.hpmFactory,
  powerbi.factories.wpmpFactory,
  powerbi.factories.routerFactory,
);

type EmbedState =
  | { kind: 'idle' }
  | { kind: 'loading' }
  | { kind: 'ready'; config: EmbedConfig }
  | { kind: 'error'; error: ApiError };

type ParityState =
  | { kind: 'idle' }
  | { kind: 'checking' }
  | { kind: 'done'; comparison: ParityComparison }
  | { kind: 'error'; message: string };

async function exportFirstTable(report: powerbi.Report): Promise<string> {
  const page = (await report.getPages())[0];
  if (!page) throw new Error('The report has no pages.');
  const table = (await page.getVisuals()).find(visual => visual.type.toLowerCase().includes('table'));
  if (!table) throw new Error('No table visual was found on the first page.');
  return (await table.exportData(powerbi.models.ExportDataType.Summarized)).data;
}

function ParitySummary({ comparison }: { comparison: ParityComparison }) {
  return (
    <div className="parity-result">
      <div className={`parity-verdict ${comparison.match ? 'parity-match' : 'parity-mismatch'}`} role="status">
        {comparison.match ? 'Match' : 'Mismatch'}
      </div>
      {comparison.differences.length > 0 && (
        <div className="table-scroll" role="region" aria-label="Parity differences" tabIndex={0}>
          <table>
            <thead><tr><th>Customer | Product</th><th>Issue</th><th>Report</th><th>Gateway</th></tr></thead>
            <tbody>
              {comparison.differences.map(diff => (
                <tr key={`${diff.kind}-${diff.key}`}>
                  <td>{diff.key}</td>
                  <td>{diff.message}</td>
                  <td>{diff.report ? `${diff.report['Total Amount']} / ${diff.report['Activity Count']}` : '—'}</td>
                  <td>{diff.gateway ? `${diff.gateway['Total Amount']} / ${diff.gateway['Activity Count']}` : '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

export function EmbeddedReport({ token }: { token: string }) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const reportRef = useRef<powerbi.Report | null>(null);
  const [embedState, setEmbedState] = useState<EmbedState>({ kind: 'idle' });
  const [parityState, setParityState] = useState<ParityState>({ kind: 'idle' });

  async function loadReport(): Promise<void> {
    setEmbedState({ kind: 'loading' });
    setParityState({ kind: 'idle' });
    try {
      const config = await loadEmbed(token);
      if (!containerRef.current) throw new Error('Missing report container.');
      powerbiService.reset(containerRef.current);
      reportRef.current = powerbiService.embed(containerRef.current, {
        type: 'report',
        id: config.reportId,
        embedUrl: config.embedUrl,
        accessToken: config.token,
        tokenType: powerbi.models.TokenType.Embed,
        settings: { filterPaneEnabled: false, navContentPaneEnabled: false },
      }) as powerbi.Report;
      setEmbedState({ kind: 'ready', config });
    } catch (error) {
      setEmbedState({ kind: 'error', error: readableError(error) });
    }
  }

  async function checkParity(): Promise<void> {
    if (!reportRef.current) {
      setParityState({ kind: 'error', message: 'Load the embedded report first.' });
      return;
    }
    setParityState({ kind: 'checking' });
    try {
      const [csv, gateway] = await Promise.all([exportFirstTable(reportRef.current), loadParityRows(token)]);
      setParityState({ kind: 'done', comparison: compareParity(rowsFromCsv(csv), rowsFromResultSet(gateway)) });
    } catch (error) {
      setParityState({ kind: 'error', message: error instanceof Error ? error.message : 'Parity check failed.' });
    }
  }

  return (
    <section className="embed-panel" aria-labelledby="embed-heading">
      <div className="section-heading">
        <div>
          <span className="eyebrow">Embedded report</span>
          <h3 id="embed-heading">Power BI Embedded for the same user</h3>
        </div>
        <div className="button-row">
          <button className="secondary-button" onClick={() => void loadReport()}>{embedState.kind === 'ready' ? 'Reload report' : 'Load report'}</button>
          <button className="primary-button" disabled={embedState.kind !== 'ready' || parityState.kind === 'checking'} onClick={() => void checkParity()}>Check parity</button>
        </div>
      </div>
      {embedState.kind === 'error' && <p className="inline-error" role="alert">{embedState.error.message}</p>}
      {embedState.kind === 'ready' && <p className="small-label">Embed token expires {new Date(embedState.config.expiration).toLocaleString()}.</p>}
      <div ref={containerRef} className="report-container" aria-label="Embedded Power BI report" />
      {parityState.kind === 'checking' && <p role="status" className="small-label">Comparing report export with the gateway query…</p>}
      {parityState.kind === 'error' && <p className="inline-error" role="alert">{parityState.message}</p>}
      {parityState.kind === 'done' && <ParitySummary comparison={parityState.comparison} />}
    </section>
  );
}
