import type { ResultSet } from './api';

export interface ParityRow {
  Customer: string;
  Product: string;
  'Total Amount': string;
  'Activity Count': string;
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

export function parseCsv(text: string): string[][] {
  const rows: string[][] = [];
  let row: string[] = [];
  let cell = '';
  let inQuotes = false;

  for (let index = 0; index < text.length; index += 1) {
    const char = text[index];
    if (inQuotes) {
      if (char === '"') {
        if (text[index + 1] === '"') {
          cell += '"';
          index += 1;
        } else {
          inQuotes = false;
        }
      } else {
        cell += char;
      }
      continue;
    }

    if (char === '"') {
      inQuotes = true;
    } else if (char === ',') {
      row.push(cell);
      cell = '';
    } else if (char === '\n') {
      row.push(cell);
      rows.push(row);
      row = [];
      cell = '';
    } else if (char !== '\r') {
      cell += char;
    }
  }

  if (inQuotes) throw new Error('CSV ended inside a quoted field.');
  if (cell.length > 0 || row.length > 0) {
    row.push(cell);
    rows.push(row);
  }
  return rows.filter(item => item.some(value => value.length > 0));
}

function rowKey(row: ParityRow): string {
  return `${row.Customer}|${row.Product}`;
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

function decimalValue(value: string): number {
  const trimmed = value.trim();
  const negative = /^\(.*\)$/.test(trimmed) || trimmed.startsWith('-');
  const stripped = trimmed.replace(/[,$\s()]/g, '').replace(/^[-+]/, '');
  const parsed = Number.parseFloat(stripped);
  if (!Number.isFinite(parsed)) return Number.NaN;
  return negative ? -parsed : parsed;
}

function closeEnough(left: string, right: string): boolean {
  const leftNumber = decimalValue(left);
  const rightNumber = decimalValue(right);
  if (Number.isFinite(leftNumber) && Number.isFinite(rightNumber)) return Math.abs(leftNumber - rightNumber) <= 0.005 + 1e-9;
  return left.trim() === right.trim();
}

function makeRow(values: Record<string, string>): ParityRow {
  return {
    Customer: values.Customer ?? '',
    Product: values.Product ?? '',
    'Total Amount': values['Total Amount'] ?? '',
    'Activity Count': values['Activity Count'] ?? '',
  };
}

export function rowsFromCsv(text: string): ParityRow[] {
  const rows = parseCsv(text);
  if (rows.length === 0) return [];
  const headers = rows[0].map(normalizeHeader);
  const positions = new Map(headers.map((header, index) => [header, index]));
  for (const column of requiredColumns) {
    if (!positions.has(column)) throw new Error(`CSV export is missing ${column}.`);
  }
  return rows.slice(1).map(csvRow => makeRow(Object.fromEntries(requiredColumns.map(column => [column, csvRow[positions.get(column) ?? -1] ?? '']))));
}

export function rowsFromResultSet(result: ResultSet): ParityRow[] {
  const positions = new Map(result.columns.map((column, index) => [normalizeHeader(column.name), index]));
  for (const column of requiredColumns) {
    if (!positions.has(column)) throw new Error(`Gateway result is missing ${column}.`);
  }
  return result.rows.map(row => makeRow(Object.fromEntries(requiredColumns.map(column => [column, row[positions.get(column) ?? -1] ?? '']))));
}

export function compareParity(reportRows: ParityRow[], gatewayRows: ParityRow[]): ParityComparison {
  const differences: ParityDifference[] = [];
  const report = new Map(reportRows.map(row => [rowKey(row), row]));
  const gateway = new Map(gatewayRows.map(row => [rowKey(row), row]));

  for (const [key, gatewayRow] of gateway) {
    const reportRow = report.get(key);
    if (!reportRow) {
      differences.push({ key, kind: 'missing_in_report', gateway: gatewayRow, message: 'Gateway row is missing from the embedded table export.' });
      continue;
    }
    const amountMatches = closeEnough(reportRow['Total Amount'], gatewayRow['Total Amount']);
    const countMatches = closeEnough(reportRow['Activity Count'], gatewayRow['Activity Count']);
    if (!amountMatches || !countMatches) {
      differences.push({ key, kind: 'value_mismatch', report: reportRow, gateway: gatewayRow, message: 'Metric values differ after numeric normalization.' });
    }
  }

  for (const [key, reportRow] of report) {
    if (!gateway.has(key)) {
      differences.push({ key, kind: 'missing_in_gateway', report: reportRow, message: 'Embedded report row is missing from the gateway baseline query.' });
    }
  }

  return { match: differences.length === 0, reportRows, gatewayRows, differences };
}


