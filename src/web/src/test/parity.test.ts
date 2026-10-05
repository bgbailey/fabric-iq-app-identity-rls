import { createElement } from 'react';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { EmbeddedReport } from '../EmbeddedReport';
import { compareParity, parseCsv, rowsFromCsv, rowsFromResultSet } from '../parity';
import type { ParityDataset } from '../parity';
import type { ResultSet } from '../api';

const embedMocks = vi.hoisted(() => ({
  loadEmbed: vi.fn(),
  loadParityRows: vi.fn(),
  embed: vi.fn(),
  reset: vi.fn(),
  exportData: vi.fn(),
}));

vi.mock('powerbi-client', () => ({
  service: { Service: class { embed = embedMocks.embed; reset = embedMocks.reset; } },
  factories: { hpmFactory: {}, wpmpFactory: {}, routerFactory: {} },
  models: { ExportDataType: { Summarized: 0 }, TokenType: { Embed: 1 } },
}));

vi.mock('../api', async importOriginal => ({
  ...await importOriginal<typeof import('../api')>(),
  loadEmbed: embedMocks.loadEmbed,
  loadParityRows: embedMocks.loadParityRows,
}));

const headers = ['Customer', 'Product', 'Total Amount', 'Activity Count'];
const csvHeader = headers.join(',');
const validCsv = `${csvHeader}\n"Contoso, Ltd.","Learning ""Kit""","$1,234.56",4.000\n`;
const gateway: ResultSet = {
  columns: headers.map(name => ({ name, type: '' })),
  rows: [['Contoso, Ltd.', 'Learning "Kit"', '1234.565', '4']],
};

function result(rows: (string | null)[][], columnNames = headers): ResultSet {
  return { columns: columnNames.map(name => ({ name, type: '' })), rows };
}

describe('CSV parser and parity comparison', () => {
  it('parses quoted CSV fields', () => {
    expect(parseCsv('Customer,Product\n"Contoso, Ltd.","Learning ""Kit"""\n')).toEqual([
      ['Customer', 'Product'],
      ['Contoso, Ltd.', 'Learning "Kit"'],
    ]);
  });

  it('preserves the supported export fixture, amount tolerance, and zero-only count decimals', () => {
    const reportRows = rowsFromCsv(validCsv);
    const comparison = compareParity(reportRows, rowsFromResultSet(gateway));
    expect(comparison.match).toBe(true);
    expect(reportRows.rows[0].Customer).toBe('Contoso, Ltd.');
    expect(reportRows.rows[0].Product).toBe('Learning "Kit"');
  });

  it('matches table-qualified gateway columns with plain CSV headers', () => {
    const qualifiedGateway: ResultSet = {
      columns: [
        { name: 'Scope[Customer]', type: '' },
        { name: "'Scope'[Product]", type: '' },
        { name: '[Total Amount]', type: '' },
        { name: '[Activity Count]', type: '' },
      ],
      rows: [['Contoso, Ltd.', 'Learning "Kit"', '1234.565', '4']],
    };

    const reportRows = rowsFromCsv('Customer,Product,Total Amount,Activity Count\n"Contoso, Ltd.","Learning ""Kit""","$1,234.56",4\n');
    expect(compareParity(reportRows, rowsFromResultSet(qualifiedGateway)).match).toBe(true);
  });

  it('also validates quoted and qualified CSV headers', () => {
    const csv = '"Scope[Customer]","\'Scope\'[Product]","[Total Amount]","[Activity Count]"\r\nA,P,10,4\r\n';
    expect(compareParity(rowsFromCsv(csv), rowsFromResultSet(result([['A', 'P', '10', '4']]))).match).toBe(true);
  });

  it('reports mismatches as set differences or value differences', () => {
    const reportRows = rowsFromCsv('Customer,Product,Total Amount,Activity Count\n"Contoso, Ltd.","Learning ""Kit""",1000,4\nExtra,Product,1,1\n');
    const comparison = compareParity(reportRows, rowsFromResultSet(gateway));
    expect(comparison.match).toBe(false);
    expect(comparison.differences.map(diff => diff.kind)).toEqual(['value_mismatch', 'missing_in_gateway']);
  });

  it('does not collide (A|B,C) with (A,B|C)', () => {
    const reportRows = rowsFromCsv(`${csvHeader}\nA|B,C,10,1\nA,B|C,20,2\n`);
    const gatewayRows = rowsFromResultSet(result([['A', 'B|C', '20', '2']]));
    const comparison = compareParity(reportRows, gatewayRows);
    expect(comparison.match).toBe(false);
    expect(comparison.differences).toMatchObject([{ key: '["A|B","C"]', kind: 'missing_in_gateway' }]);
  });

  it('ignores row order without losing distinct tuples', () => {
    const reportRows = rowsFromCsv(`${csvHeader}\nA|B,C,10,1\nA,B|C,20,2\n`);
    const gatewayRows = rowsFromResultSet(result([['A', 'B|C', '20', '2'], ['A|B', 'C', '10', '1']]));
    expect(compareParity(reportRows, gatewayRows).match).toBe(true);
  });

  it.each(['Report', 'Gateway'])('rejects duplicate tuples on the %s side even with identical metrics', side => {
    const unique = result([['A', 'P', '10', '4']]);
    const duplicate = result([['A', 'P', '10', '4'], ['A', 'P', '10', '4']]);
    const reportRows = rowsFromCsv(`${csvHeader}\nA,P,10,4\n${side === 'Report' ? 'A,P,10,4\n' : ''}`);
    expect(() => compareParity(reportRows, rowsFromResultSet(side === 'Gateway' ? duplicate : unique))).toThrow(/duplicate Customer\/Product tuple/);
  });

  it('compares counts exactly instead of applying amount tolerance', () => {
    const reportRows = rowsFromCsv(`${csvHeader}\nA,P,10,4.000\n`);
    const comparison = compareParity(reportRows, rowsFromResultSet(result([['A', 'P', '10', '5']])));
    expect(comparison.match).toBe(false);
    expect(comparison.differences[0].kind).toBe('value_mismatch');
  });

  it.each(['4.004', '4.000000000000000000001', '-1', '1M', 'NaN', 'Infinity', '9007199254740993'])('rejects invalid or inexact count %j on either side', count => {
    expect(() => rowsFromCsv(`${csvHeader}\nA,P,10,${count}\n`)).toThrow();
    expect(() => rowsFromResultSet(result([['A', 'P', '10', count]]))).toThrow();
  });

  it.each(['1M', 'junk', '', ' ', 'NaN', 'Infinity', '-Infinity', '1e309', '0x10', '1(2)'])('rejects invalid or nonfinite amount %j on either side', amount => {
    expect(() => rowsFromCsv(`${csvHeader}\nA,P,${amount},4\n`)).toThrow();
    expect(() => rowsFromResultSet(result([['A', 'P', amount, '4']]))).toThrow();
  });

  it('does not fall back to equality for invalid metrics passed to the comparator', () => {
    const reportRows = rowsFromCsv(validCsv);
    const gatewayRows = rowsFromResultSet(gateway);
    reportRows.rows[0]['Total Amount'] = 'junk';
    gatewayRows.rows[0]['Total Amount'] = 'junk';
    expect(() => compareParity(reportRows, gatewayRows)).toThrow(/Invalid baseline numeric/);
  });

  it.each([
    ['Customer', 'Product', 'Total Amount'],
    ['Customer', 'Product', 'Total Amount', 'Total Amount', 'Activity Count'],
    ['Customer', 'Scope[Customer]', 'Product', 'Total Amount', 'Activity Count'],
    ['Unknown', 'Product', 'Total Amount', 'Activity Count'],
    ['', 'Product', 'Total Amount', 'Activity Count'],
  ])('rejects missing, duplicate, or unknown headers %j even for empty data', (...columnNames) => {
    expect(() => rowsFromCsv(columnNames.join(','))).toThrow();
    expect(() => rowsFromResultSet(result([], columnNames))).toThrow();
  });

  it.each([
    ['A', 'P', '10'],
    ['A', 'P', '10', '4', 'unknown'],
    ['', 'P', '10', '4'],
    ['A', ' ', '10', '4'],
  ])('rejects missing or unknown cells %j', (...cells) => {
    expect(() => rowsFromCsv(`${csvHeader}\n${cells.join(',')}\n`)).toThrow();
    expect(() => rowsFromResultSet(result([cells]))).toThrow();
  });

  it.each([0, 1, 2, 3])('rejects a null gateway cell at position %i', position => {
    const cells: (string | null)[] = ['A', 'P', '10', '4'];
    cells[position] = null;
    expect(() => rowsFromResultSet(result([cells]))).toThrow(/missing or invalid/);
  });

  it.each([
    `${csvHeader}\n"A,P,10,4`,
    `${csvHeader}\n"A"junk,P,10,4`,
    `${csvHeader}\nA"B,P,10,4`,
    `${csvHeader}\n,,,`,
    `${csvHeader}\n\n`,
  ])('rejects malformed CSV instead of discarding invalid records', csv => {
    expect(() => rowsFromCsv(csv)).toThrow();
  });

  it('retains quoted line breaks and escaped quotes', () => {
    expect(parseCsv('"A\r\nB","C""D"\r\n')).toEqual([['A\r\nB', 'C"D']]);
  });

  it('accepts two schema-bearing empty inputs as verified-empty agreement', () => {
    const comparison = compareParity(rowsFromCsv(`${csvHeader}\n`), rowsFromResultSet(result([])));
    expect(comparison).toMatchObject({ match: true, reportRows: [], gatewayRows: [], differences: [] });
  });

  it.each(['Report', 'Gateway'])('mismatches when only %s is empty', side => {
    const reportRows = rowsFromCsv(side === 'Report' ? csvHeader : validCsv);
    const gatewayRows = rowsFromResultSet(side === 'Gateway' ? result([]) : gateway);
    const comparison = compareParity(reportRows, gatewayRows);
    expect(comparison.match).toBe(false);
    expect(comparison.differences[0].kind).toBe(side === 'Report' ? 'missing_in_report' : 'missing_in_gateway');
  });

  it.each(['', '\n', 'A,P,10,4', null, undefined])('rejects missing or headerless CSV %j rather than treating it as no access', csv => {
    expect(() => rowsFromCsv(csv as string)).toThrow();
  });

  it.each([null, undefined, {}, { rows: [] }, { columns: gateway.columns }, { columns: [], rows: [] }])('rejects missing gateway data or schema %j', value => {
    expect(() => rowsFromResultSet(value as ResultSet)).toThrow();
  });

  it('requires validated adapter datasets rather than bare empty arrays', () => {
    expect(() => compareParity([] as unknown as ParityDataset, [] as unknown as ParityDataset)).toThrow(/schema has not been validated/);
    expect(() => compareParity({ rows: [] } as unknown as ParityDataset, rowsFromResultSet(result([])))).toThrow(/schema has not been validated/);
  });

  it.each([{ isError: true }, { truncated: true }, { warnings: ['Incomplete data'] }, { warnings: {} }])('rejects known gateway failure or warning %j', status => {
    expect(() => rowsFromResultSet({ ...result([]), ...status })).toThrow(/failed or has incomplete-data warnings/);
  });

  it('rejects warning records in the CSV export', () => {
    expect(() => rowsFromCsv(`${validCsv}Data may be incomplete\n`)).toThrow();
  });

  it('rejects cap-sized inputs instead of claiming export completeness', () => {
    expect(() => rowsFromCsv(`${csvHeader}\n${'A,P,10,4\n'.repeat(30_000)}`)).toThrow(/export limit/);
    expect(() => rowsFromResultSet(result(Array.from({ length: 30_000 }, () => ['A', 'P', '10', '4'])))).toThrow(/export limit/);
  });
});

describe('embedded baseline verdict', () => {
  const handlers = new Map<string, (event: { detail?: { message: string } }) => void>();

  beforeEach(() => {
    handlers.clear();
    for (const mock of Object.values(embedMocks)) mock.mockReset();
    embedMocks.loadEmbed.mockResolvedValue({ reportId: 'synthetic-report', embedUrl: 'https://example.invalid', token: 'synthetic-token', expiration: '2026-10-05T01:00:00Z' });
    embedMocks.loadParityRows.mockResolvedValue(gateway);
    embedMocks.exportData.mockResolvedValue({ data: validCsv });
    embedMocks.embed.mockReturnValue({
      on: (name: string, handler: (event: { detail?: { message: string } }) => void) => handlers.set(name, handler),
      getPages: async () => [{ getVisuals: async () => [{ type: 'table', exportData: embedMocks.exportData }] }],
    });
  });

  afterEach(cleanup);

  async function loadRenderedReport(): Promise<void> {
    render(createElement(EmbeddedReport, { token: 'synthetic-session' }));
    fireEvent.click(screen.getByRole('button', { name: 'Load report' }));
    await screen.findByText('Waiting for the embedded report to render…');
    act(() => handlers.get('rendered')!({}));
  }

  async function baselineMatch(): Promise<void> {
    await loadRenderedReport();
    fireEvent.click(screen.getByRole('button', { name: 'Check parity' }));
    await screen.findByText('Baseline aggregate match');
  }

  it('gates checking on rendering and qualifies a positive baseline verdict', async () => {
    render(createElement(EmbeddedReport, { token: 'synthetic-session' }));
    fireEvent.click(screen.getByRole('button', { name: 'Load report' }));
    await screen.findByText('Waiting for the embedded report to render…');
    expect(screen.getByRole('button', { name: 'Check parity' })).toBeDisabled();
    expect(embedMocks.loadParityRows).not.toHaveBeenCalled();
    act(() => handlers.get('rendered')!({}));
    fireEvent.click(screen.getByRole('button', { name: 'Check parity' }));
    await screen.findByText('Baseline aggregate match');
    expect(screen.getByText(/not RLS certification/)).toBeInTheDocument();
  });

  it('shows explicit verified-empty agreement only with validated empty inputs', async () => {
    embedMocks.exportData.mockResolvedValue({ data: csvHeader });
    embedMocks.loadParityRows.mockResolvedValue(result([]));
    await loadRenderedReport();
    fireEvent.click(screen.getByRole('button', { name: 'Check parity' }));
    await screen.findByText('Verified-empty agreement: no visible baseline aggregates.');
  });

  it('clears the old match for a new check and shows malformed-input failures in the alert', async () => {
    await baselineMatch();
    embedMocks.exportData.mockResolvedValueOnce({ data: '' });
    fireEvent.click(screen.getByRole('button', { name: 'Check parity' }));
    expect(screen.queryByText('Baseline aggregate match')).not.toBeInTheDocument();
    expect(await screen.findByRole('alert')).toHaveTextContent('missing required headers');
    expect(screen.queryByText(/Verified-empty agreement/)).not.toBeInTheDocument();
  });

  it('prevents an in-flight check from restoring green after reload', async () => {
    await baselineMatch();
    let resolveExport!: (value: { data: string }) => void;
    embedMocks.exportData.mockReturnValueOnce(new Promise<{ data: string }>(resolve => { resolveExport = resolve; }));
    fireEvent.click(screen.getByRole('button', { name: 'Check parity' }));
    await waitFor(() => expect(embedMocks.exportData).toHaveBeenCalledTimes(2));
    fireEvent.click(screen.getByRole('button', { name: 'Reload report' }));
    await screen.findByText('Waiting for the embedded report to render…');
    await act(async () => resolveExport({ data: validCsv }));
    expect(screen.queryByText('Baseline aggregate match')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Check parity' })).toBeDisabled();
  });

  it('clears a positive verdict on an embedded report failure', async () => {
    await baselineMatch();
    act(() => handlers.get('error')!({ detail: { message: 'Synthetic report failure' } }));
    expect(screen.getByRole('alert')).toHaveTextContent('Synthetic report failure');
    expect(screen.queryByText('Baseline aggregate match')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Check parity' })).toBeDisabled();
  });

  it('clears a positive verdict even when reload fails', async () => {
    await baselineMatch();
    embedMocks.loadEmbed.mockRejectedValueOnce(new Error('Synthetic reload failure'));
    fireEvent.click(screen.getByRole('button', { name: 'Reload report' }));
    expect(screen.queryByText('Baseline aggregate match')).not.toBeInTheDocument();
    expect(await screen.findByRole('alert')).toHaveTextContent('Synthetic reload failure');
    expect(screen.getByRole('button', { name: 'Check parity' })).toBeDisabled();
  });

  it('surfaces export warnings without a match or empty success', async () => {
    embedMocks.exportData.mockResolvedValue({ data: csvHeader, warnings: ['Incomplete data'] });
    embedMocks.loadParityRows.mockResolvedValue(result([]));
    await loadRenderedReport();
    fireEvent.click(screen.getByRole('button', { name: 'Check parity' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('incomplete-data warnings');
    expect(screen.queryByText(/agreement|Baseline aggregate match/)).not.toBeInTheDocument();
  });
});
