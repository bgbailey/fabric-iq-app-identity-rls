using System.Globalization;
using Apache.Arrow;
using Apache.Arrow.Compression;
using Apache.Arrow.Ipc;
using Apache.Arrow.Memory;
using Apache.Arrow.Types;

namespace SemanticGateway.Fabric;

public sealed record ResultColumn(string Name, string Type);
public sealed record QueryResult(IReadOnlyList<ResultColumn> Columns, IReadOnlyList<string?[]> Rows);

/// <summary>
/// Reads the Apache Arrow response of executeDaxQueries. The body can hold several Arrow IPC
/// streams back to back: one per result set, plus an error stream (schema metadata
/// IsError=true) when the query fails. Values are returned as invariant strings.
/// </summary>
public static class ArrowResults
{
    public static QueryResult Parse(byte[] payload)
    {
        using var stream = new MemoryStream(payload);
        QueryResult? result = null;
        while (stream.Position < stream.Length)
        {
            using var reader = new ArrowStreamReader(stream, MemoryAllocator.Default.Value, new CompressionCodecFactory(), leaveOpen: true);
            var schema = reader.Schema;
            var rows = new List<string?[]>();
            while (reader.ReadNextRecordBatch() is { } batch)
            {
                using (batch)
                {
                    for (var row = 0; row < batch.Length; row++)
                        rows.Add(Enumerable.Range(0, batch.ColumnCount).Select(column => Read(batch.Column(column), row)).ToArray());
                }
            }

            if (IsError(schema))
            {
                // Error rowsets carry FaultString metadata and an ErrorMessage column.
                var message = schema.Metadata?.GetValueOrDefault("FaultString");
                var column = schema.FieldsList.Select(f => f.Name).ToList().IndexOf("ErrorMessage");
                message ??= column >= 0 && rows.Count > 0 ? rows[0][column] : null;
                throw new ToolException("The DAX query failed: " + (message ?? "unknown error"));
            }
            result ??= new QueryResult(schema.FieldsList.Select(f => new ResultColumn(f.Name, TypeName(f.DataType))).ToList(), rows);
        }
        return result ?? throw new ToolException("The query returned no result set.");
    }

    private static bool IsError(Schema schema) =>
        schema.Metadata?.Any(m => m.Key.Equals("IsError", StringComparison.OrdinalIgnoreCase) && bool.TryParse(m.Value, out var isError) && isError) == true;

    private static string TypeName(IArrowType type) => type switch
    {
        StringType => "string",
        Int8Type or Int16Type or Int32Type or Int64Type or UInt8Type or UInt16Type or UInt32Type or UInt64Type => "integer",
        FloatType or DoubleType or Decimal128Type => "number",
        BooleanType => "boolean",
        Date32Type or Date64Type => "date",
        TimestampType => "datetime",
        DictionaryType dictionary => TypeName(dictionary.ValueType),
        _ => type.Name
    };

    private static string? Read(IArrowArray array, int index)
    {
        if (array.IsNull(index)) return null;
        var c = CultureInfo.InvariantCulture;
        return array switch
        {
            StringArray a => a.GetString(index),
            Int8Array a => a.GetValue(index)?.ToString(c),
            Int16Array a => a.GetValue(index)?.ToString(c),
            Int32Array a => a.GetValue(index)?.ToString(c),
            Int64Array a => a.GetValue(index)?.ToString(c),
            UInt8Array a => a.GetValue(index)?.ToString(c),
            UInt16Array a => a.GetValue(index)?.ToString(c),
            UInt32Array a => a.GetValue(index)?.ToString(c),
            UInt64Array a => a.GetValue(index)?.ToString(c),
            FloatArray a => a.GetValue(index)?.ToString("R", c),
            DoubleArray a => a.GetValue(index)?.ToString("R", c),
            Decimal128Array a => a.GetValue(index)?.ToString(c),
            BooleanArray a => a.GetValue(index) == true ? "true" : "false",
            Date32Array a => a.GetDateTime(index)?.ToString("yyyy-MM-dd", c),
            Date64Array a => a.GetDateTime(index)?.ToString("yyyy-MM-dd", c),
            TimestampArray a => a.GetTimestamp(index)?.ToString("o", c),
            DictionaryArray a => Read(a.Dictionary, int.Parse(Read(a.Indices, index)!, c)),
            _ => throw new ToolException($"Unsupported Arrow type {array.Data.DataType.Name}.")
        };
    }
}
