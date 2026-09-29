import { describe, expect, it } from 'vitest';
import { compareParity, parseCsv, rowsFromCsv, rowsFromResultSet } from '../parity';
import type { ResultSet } from '../api';

const gateway: ResultSet = {
  columns: [
    { name: 'Customer', type: '' },
    { name: 'Product', type: '' },
    { name: 'Total Amount', type: '' },
    { name: 'Activity Count', type: '' },
  ],
  rows: [['Contoso, Ltd.', 'Learning "Kit"', '1234.565', '4']],
};

describe('CSV parser and parity comparison', () => {
  it('parses quoted CSV fields', () => {
    expect(parseCsv('Customer,Product\n"Contoso, Ltd.","Learning ""Kit"""\n')).toEqual([
      ['Customer', 'Product'],
      ['Contoso, Ltd.', 'Learning "Kit"'],
    ]);
  });

  it('matches rows with currency formatting and numeric tolerance', () => {
    const reportRows = rowsFromCsv('Customer,Product,Total Amount,Activity Count\n"Contoso, Ltd.","Learning ""Kit""","$1,234.56",4.004\n');
    const comparison = compareParity(reportRows, rowsFromResultSet(gateway));
    expect(comparison.match).toBe(true);
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

  it('reports mismatches as set differences or value differences', () => {
    const reportRows = rowsFromCsv('Customer,Product,Total Amount,Activity Count\n"Contoso, Ltd.","Learning ""Kit""",1000,4\nExtra,Product,1,1\n');
    const comparison = compareParity(reportRows, rowsFromResultSet(gateway));
    expect(comparison.match).toBe(false);
    expect(comparison.differences.map(diff => diff.kind)).toEqual(['value_mismatch', 'missing_in_gateway']);
  });
});

