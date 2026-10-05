import type { ResultSet } from './api';

export interface ParityRow {
  Customer: string;
  Product: string;
  'Total Amount': string;
  'Activity Count': string;
}

export interface ParityDataset {
  rows: ParityRow[];
  schemaValidated: true;
}

export interface ParityDifference {
  key: string;
  kind: 'missing_in_report' | 'missing_in_gateway' | 'value_mismatch';
  report?: ParityRow;
  gateway?: ParityRow;
  message: string;
}

export interface ParityComparison {
  match: boolean;
  reportRows: ParityRow[];
  gatewayRows: ParityRow[];
  differences: ParityDifference[];
}

const requiredColumns = ['Customer', 'Product', 'Total Amount', 'Activity Count'] as const;
const exportRowLimit = 30_000;

export function parseCsv(text: string): string[][] {
  if (typeof text !== 'string') throw new Error('CSV export is missing.');
  const rows: string[][] = [];
  let row: string[] = [];
  let cell = '';
  let inQuotes = false;
  let afterQuote = false;

  for (let index = 0; index < text.length; index += 1) {
    const char = text[index];
    if (inQuotes) {
      if (char === '"') {
        if (text[index + 1] === '"') {
          cell += '"';
          index += 1;
        } else {
          inQuotes = false;
          afterQuote = true;
        }
      } else {
        cell += char;
      }
      continue;
    }

    if (afterQuote && char !== ',' && char !== '\n' && char !== '\r') {
      throw new Error('CSV contains text after a closing quote.');
    }
    if (char === '"') {
      if (cell.length > 0) throw new Error('CSV contains a quote inside an unquoted field.');
      inQuotes = true;
    } else if (char === ',') {
      row.push(cell);
      cell = '';
      afterQuote = false;
    } else if (char === '\n' || char === '\r') {
      row.push(cell);
      rows.push(row);
      row = [];
      cell = '';
      afterQuote = false;
      if (char === '\r' && text[index + 1] === '\n') index += 1;
    } else {
      cell += char;
    }
  }

  if (inQuotes) throw new Error('CSV ended inside a quoted field.');
  if (cell.length > 0 || row.length > 0 || afterQuote) {
    row.push(cell);
    rows.push(row);
  }
  return rows;
}

function rowKey(row: ParityRow): string {
  return JSON.stringify([row.Customer, row.Product]);
}

function normalizeHeader(value: string): string {
  const text = value.trim();
  const open = text.indexOf('[');
  if (open !== -1 && text.endsWith(']')) {
    const prefix = text.slice(0, open).trim();
    if (prefix === '' || /^'[^']+'$/.test(prefix) || /^[^\[\]]+$/.test(prefix)) {
      return text.slice(open + 1, -1).replace(/]]/g, ']');
    }
  }
  return text;
}

function normalizedMetric(value: string): string {
  const trimmed = value.trim();
  const parenthesized = /^\(.*\)$/.test(trimmed);
  const stripped = (parenthesized ? trimmed.slice(1, -1) : trimmed).replace(/[,$\s]/g, '');
  if (!/^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?$/.test(stripped)
    || (parenthesized && /^[+-]/.test(stripped))) {
    throw new Error(`Invalid baseline numeric value: ${JSON.stringify(value)}.`);
  }
  return parenthesized ? `-${stripped}` : stripped;
}

function amountValue(value: string): number {
  const parsed = Number(normalizedMetric(value));
  if (!Number.isFinite(parsed)) throw new Error('Total Amount must be a finite number.');
  return parsed;
}

function countValue(value: string): number {
  const normalized = normalizedMetric(value);
  const parsed = Number(normalized);
  if (!/^\+?(?:\d+(?:\.0*)?|\.0+)$/.test(normalized) || !Number.isSafeInteger(parsed)) {
    throw new Error('Activity Count must be an exact nonnegative safe integer.');
  }
  return parsed;
}

function validateRow(row: ParityRow, source: string): void {
  for (const column of requiredColumns) {
    if (!row || typeof row[column] !== 'string' || row[column].trim().length === 0) {
      throw new Error(`${source} has a missing or invalid ${column} cell.`);
    }
  }
  amountValue(row['Total Amount']);
  countValue(row['Activity Count']);
}

function headerPositions(headers: string[], source: string): Map<string, number> {
  const positions = new Map<string, number>();
  headers.forEach((value, index) => {
    if (typeof value !== 'string') throw new Error(`${source} has an invalid header.`);
    const header = normalizeHeader(value);
    if (!requiredColumns.some(column => column === header)) {
      throw new Error(`${source} has an unknown header: ${JSON.stringify(header)}.`);
    }
    if (positions.has(header)) throw new Error(`${source} has a duplicate ${header} header.`);
    positions.set(header, index);
  });
  for (const column of requiredColumns) {
    if (!positions.has(column)) throw new Error(`${source} is missing ${column}.`);
  }
  return positions;
}

function datasetFromCells(cells: (string | null)[][], headers: string[], source: string): ParityDataset {
  const positions = headerPositions(headers, source);
  if (cells.length >= exportRowLimit) throw new Error(`${source} reached the 30,000-row export limit; baseline completeness is unverified.`);
  const rows = cells.map(cellsRow => {
    if (!Array.isArray(cellsRow) || cellsRow.length !== headers.length) {
      throw new Error(`${source} has a record with missing or extra cells.`);
    }
    const row = Object.fromEntries(requiredColumns.map(column => [column, cellsRow[positions.get(column)!]])) as unknown as ParityRow;
    validateRow(row, source);
    return row;
  });
  return { rows, schemaValidated: true };
}

export function rowsFromCsv(text: string): ParityDataset {
  const rows = parseCsv(text);
  if (rows.length === 0) throw new Error('CSV export is missing required headers.');
  return datasetFromCells(rows.slice(1), rows[0], 'CSV export');
}

export function rowsFromResultSet(result: ResultSet): ParityDataset {
  if (!result || !Array.isArray(result.columns) || !Array.isArray(result.rows)) {
    throw new Error('Gateway result is missing its baseline schema or rows.');
  }
  const status = result as ResultSet & { isError?: boolean; truncated?: boolean; warnings?: unknown[] };
  const hasWarnings = status.warnings !== undefined && (!Array.isArray(status.warnings) || status.warnings.length > 0);
  if (status.isError || status.truncated || hasWarnings) {
    throw new Error('Gateway result failed or has incomplete-data warnings.');
  }
  return datasetFromCells(result.rows, result.columns.map(column => column?.name), 'Gateway result');
}

function keyedRows(dataset: ParityDataset, source: string): Map<string, ParityRow> {
  if (!dataset || dataset.schemaValidated !== true || !Array.isArray(dataset.rows)) {
    throw new Error(`${source} baseline schema has not been validated.`);
  }
  const rows = new Map<string, ParityRow>();
  for (const row of dataset.rows) {
    validateRow(row, source);
    const key = rowKey(row);
    if (rows.has(key)) throw new Error(`${source} has a duplicate Customer/Product tuple: ${key}.`);
    rows.set(key, row);
  }
  return rows;
}

export function compareParity(reportDataset: ParityDataset, gatewayDataset: ParityDataset): ParityComparison {
  const differences: ParityDifference[] = [];
  const report = keyedRows(reportDataset, 'Report');
  const gateway = keyedRows(gatewayDataset, 'Gateway');

  for (const [key, gatewayRow] of gateway) {
    const reportRow = report.get(key);
    if (!reportRow) {
      differences.push({ key, kind: 'missing_in_report', gateway: gatewayRow, message: 'Gateway row is missing from the embedded table export.' });
      continue;
    }
    const amountMatches = Math.abs(amountValue(reportRow['Total Amount']) - amountValue(gatewayRow['Total Amount'])) <= 0.005 + 1e-9;
    const countMatches = countValue(reportRow['Activity Count']) === countValue(gatewayRow['Activity Count']);
    if (!amountMatches || !countMatches) {
      differences.push({ key, kind: 'value_mismatch', report: reportRow, gateway: gatewayRow, message: 'Metric values differ after numeric normalization.' });
    }
  }

  for (const [key, reportRow] of report) {
    if (!gateway.has(key)) {
      differences.push({ key, kind: 'missing_in_gateway', report: reportRow, message: 'Embedded report row is missing from the gateway baseline query.' });
    }
  }

  return { match: differences.length === 0, reportRows: reportDataset.rows, gatewayRows: gatewayDataset.rows, differences };
}
