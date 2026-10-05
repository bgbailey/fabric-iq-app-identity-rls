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
  | { kind: 'rendering'; config: EmbedConfig }
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
  const exported = await table.exportData(powerbi.models.ExportDataType.Summarized);
  const status = exported as typeof exported & { truncated?: boolean; warnings?: unknown[] };
  if (!exported) throw new Error('Report export is missing.');
  const hasWarnings = status.warnings !== undefined && (!Array.isArray(status.warnings) || status.warnings.length > 0);
  if (status.truncated || hasWarnings) {
    throw new Error('Report export is missing or has incomplete-data warnings.');
  }
  return exported.data;
}

function ParitySummary({ comparison }: { comparison: ParityComparison }) {
  const verifiedEmpty = comparison.match && comparison.reportRows.length === 0 && comparison.gatewayRows.length === 0;
  return (
    <div className="parity-result">
      <div className={`parity-verdict ${comparison.match ? 'parity-match' : 'parity-mismatch'}`} role="status">
        {verifiedEmpty ? 'Verified-empty agreement: no visible baseline aggregates.' : comparison.match ? 'Baseline aggregate match' : 'Baseline aggregate mismatch'}
      </div>
      {comparison.differences.length > 0 && (
        <div className="table-scroll" role="region" aria-label="Parity differences" tabIndex={0}>
          <table>
            <thead><tr><th>Customer / Product tuple</th><th>Issue</th><th>Report</th><th>Gateway</th></tr></thead>
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
  const operationRef = useRef(0);
  const [embedState, setEmbedState] = useState<EmbedState>({ kind: 'idle' });
  const [parityState, setParityState] = useState<ParityState>({ kind: 'idle' });

  async function loadReport(): Promise<void> {
    const operation = ++operationRef.current;
    reportRef.current = null;
    setEmbedState({ kind: 'loading' });
    setParityState({ kind: 'idle' });
    try {
      const config = await loadEmbed(token);
      if (operationRef.current !== operation) return;
      if (!containerRef.current) throw new Error('Missing report container.');
      powerbiService.reset(containerRef.current);
      const report = powerbiService.embed(containerRef.current, {
        type: 'report',
        id: config.reportId,
        embedUrl: config.embedUrl,
        accessToken: config.token,
        tokenType: powerbi.models.TokenType.Embed,
        settings: { filterPaneEnabled: false, navContentPaneEnabled: false },
      }) as powerbi.Report;
      reportRef.current = report;
      setEmbedState({ kind: 'rendering', config });
      report.on('rendered', () => {
        if (reportRef.current === report) setEmbedState({ kind: 'ready', config });
      });
      report.on('error', event => {
        if (reportRef.current !== report) return;
        operationRef.current += 1;
        reportRef.current = null;
        const detail = event.detail as { message?: string } | undefined;
        setEmbedState({ kind: 'error', error: readableError(new Error(detail?.message ?? 'Embedded report failed.')) });
        setParityState({ kind: 'idle' });
      });
    } catch (error) {
      if (operationRef.current !== operation) return;
      setEmbedState({ kind: 'error', error: readableError(error) });
    }
  }

  async function checkParity(): Promise<void> {
    const operation = ++operationRef.current;
    const report = reportRef.current;
    if (!report || embedState.kind !== 'ready') {
      setParityState({ kind: 'error', message: 'Load the embedded report and wait for it to render first.' });
      return;
    }
    setParityState({ kind: 'checking' });
    try {
      const [csv, gateway] = await Promise.all([exportFirstTable(report), loadParityRows(token)]);
      if (operationRef.current !== operation || reportRef.current !== report) return;
      setParityState({ kind: 'done', comparison: compareParity(rowsFromCsv(csv), rowsFromResultSet(gateway)) });
    } catch (error) {
      if (operationRef.current !== operation || reportRef.current !== report) return;
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
          <button className="secondary-button" disabled={embedState.kind === 'loading'} onClick={() => void loadReport()}>{embedState.kind === 'ready' || embedState.kind === 'rendering' ? 'Reload report' : 'Load report'}</button>
          <button className="primary-button" disabled={embedState.kind !== 'ready' || parityState.kind === 'checking'} onClick={() => void checkParity()}>Check parity</button>
        </div>
      </div>
      <p className="small-label">Supporting cross-check for this controlled synthetic baseline only: fixed Customer/Product totals and counts from one exported table. It does not compare arbitrary chat answers, underlying row membership, or synchronized report filters, and is not RLS certification. Keep report filters unchanged; export completeness outside this baseline is unverified.</p>
      {embedState.kind === 'error' && <p className="inline-error" role="alert">{embedState.error.message}</p>}
      {embedState.kind === 'rendering' && <p className="small-label" role="status">Waiting for the embedded report to render…</p>}
      {embedState.kind === 'ready' && <p className="small-label">Embed token expires {new Date(embedState.config.expiration).toLocaleString()}.</p>}
      <div ref={containerRef} className="report-container" aria-label="Embedded Power BI report" />
      {parityState.kind === 'checking' && <p role="status" className="small-label">Comparing report export with the gateway query…</p>}
      {parityState.kind === 'error' && <p className="inline-error" role="alert">{parityState.message}</p>}
      {parityState.kind === 'done' && <ParitySummary comparison={parityState.comparison} />}
    </section>
  );
}
