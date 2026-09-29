using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using Apache.Arrow;
using Apache.Arrow.Compression;
using Apache.Arrow.Ipc;
using Apache.Arrow.Memory;
using Apache.Arrow.Types;

namespace IqRls.Core;

// Values are lossless invariant strings with explicit Arrow type descriptors; null remains JSON null.
// In particular int64/decimal are never routed through a JSON/JavaScript double.
public sealed record ResultColumn(string Name, string ArrowType, bool Nullable);
public sealed record QueryResult(ImmutableArray<ResultColumn> Columns, ImmutableArray<ImmutableArray<string?>> Rows);

public static class ArrowResponseParser
{
    public static QueryResult Parse(byte[] payload, CancellationToken cancellationToken = default)
    {
        if (payload.Length == 0) throw new HarnessException(FailureCode.InvalidArrow);
        if (payload.Length > HarnessContract.MaxResponseBytes) throw new HarnessException(FailureCode.LimitExceeded);
        try
        {
            var streams = ArrowFraming.Split(payload);
            var allocator = new BudgetAllocator();
            QueryResult? result = null;
            var errorFound = false;
            var dataStreams = 0;
            var outputCharacters = 0;
            foreach (var (offset, length) in streams)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var stream = new MemoryStream(payload, offset, length, writable: false);
                using var reader = new ArrowStreamReader(stream, allocator, new CompressionCodecFactory(), leaveOpen: false);
                var schema = reader.Schema ?? throw new HarnessException(FailureCode.InvalidArrow);
                var isError = IsError(schema);
                errorFound |= isError;
                if (!isError) dataStreams++;
                if (schema.FieldsList.Count > HarnessContract.MaxColumns)
                    throw new HarnessException(FailureCode.LimitExceeded);
                var columns = ImmutableArray.CreateBuilder<ResultColumn>();
                if (!isError)
                {
                    if (schema.FieldsList.Count == 0) throw new HarnessException(FailureCode.InvalidArrow);
                    foreach (var field in schema.FieldsList)
                    {
                        if (field.Name.Length > 256) throw new HarnessException(FailureCode.LimitExceeded);
                        columns.Add(new(field.Name, Describe(field.DataType), field.IsNullable));
                    }
                    if (columns.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != columns.Count)
                        throw new HarnessException(FailureCode.InvalidArrow);
                }
                var rows = ImmutableArray.CreateBuilder<ImmutableArray<string?>>();
                var batchRows = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var batch = reader.ReadNextRecordBatch();
                    if (batch is null) break;
                    batchRows = checked(batchRows + batch.Length);
                    // The service row cap could have cut off a larger result. Do not report it as complete.
                    if (batchRows >= HarnessContract.MaxRows) throw new HarnessException(FailureCode.LimitExceeded);
                    if (batch.ColumnCount != schema.FieldsList.Count)
                        throw new HarnessException(FailureCode.InvalidArrow);
                    for (var col = 0; col < batch.ColumnCount; col++)
                        if (batch.Column(col).Length != batch.Length ||
                            batch.Column(col).NullCount < 0 || batch.Column(col).NullCount > batch.Length)
                            throw new HarnessException(FailureCode.InvalidArrow);
                    // Error fault text is intentionally never materialized in output or exceptions.
                    if (isError) continue;
                    for (var row = 0; row < batch.Length; row++)
                    {
                        var values = ImmutableArray.CreateBuilder<string?>(batch.ColumnCount);
                        for (var col = 0; col < batch.ColumnCount; col++)
                        {
                            var value = ReadValue(batch.Column(col), row);
                            if (value is null && !schema.GetFieldByIndex(col).IsNullable)
                                throw new HarnessException(FailureCode.InvalidArrow);
                            if (value?.Length > HarnessContract.MaxCellCharacters)
                                throw new HarnessException(FailureCode.LimitExceeded);
                            outputCharacters = checked(outputCharacters + (value?.Length ?? 0));
                            if (outputCharacters > HarnessContract.MaxOutputCharacters)
                                throw new HarnessException(FailureCode.LimitExceeded);
                            values.Add(value);
                        }
                        rows.Add(values.ToImmutable());
                    }
                }
                if (stream.Position != stream.Length) throw new HarnessException(FailureCode.InvalidArrow);
                if (!isError) result = new(columns.ToImmutable(), rows.ToImmutable());
            }
            if (errorFound) throw new HarnessException(FailureCode.QueryError);
            if (dataStreams != 1 || result is null) throw new HarnessException(FailureCode.UnexpectedResultSets);
            return result;
        }
        catch (HarnessException) { throw; }
        catch (OperationCanceledException) { throw; }
        // Arrow 22.1 throws even plain Exception on invalid FlatBuffers. This single untrusted-data
        // boundary scrubs SDK messages; it never treats a parse exception as successful end-of-stream.
        catch (Exception error) when (error is not OutOfMemoryException and not AccessViolationException)
        {
            throw new HarnessException(FailureCode.InvalidArrow);
        }
    }

    private static bool IsError(Schema schema)
    {
        var values = schema.Metadata?.Where(p => p.Key.Equals("IsError", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (values is not { Length: > 0 }) return false;
        if (values.Length != 1 || !bool.TryParse(values[0].Value, out var result))
            throw new HarnessException(FailureCode.InvalidArrow);
        return result;
    }

    private static string Describe(IArrowType type) => type switch
    {
        Int8Type => "int8", Int16Type => "int16", Int32Type => "int32", Int64Type => "int64",
        UInt8Type => "uint8", UInt16Type => "uint16", UInt32Type => "uint32", UInt64Type => "uint64",
        BooleanType => "bool", StringType => "utf8", FloatType => "float32", DoubleType => "float64",
        NullType => "null", Date32Type => "date32", Date64Type => "date64",
        Decimal128Type d when d.Scale is >= -38 and <= 38 => $"decimal128({d.Precision},{d.Scale})",
        TimestampType t when (t.Timezone?.Length ?? 0) <= 128 =>
            $"timestamp({t.Unit},timezone={t.Timezone ?? "unspecified"};value=epoch-units)",
        DictionaryType d => $"dictionary({Describe(d.IndexType)},{Describe(d.ValueType)})",
        _ => throw new HarnessException(FailureCode.UnsupportedType)
    };

    private static string? ReadValue(IArrowArray array, int index)
    {
        if (array.IsNull(index)) return null;
        var invariant = CultureInfo.InvariantCulture;
        return array switch
        {
            Int8Array a => a.GetValue(index)!.Value.ToString(invariant),
            Int16Array a => a.GetValue(index)!.Value.ToString(invariant),
            Int32Array a => a.GetValue(index)!.Value.ToString(invariant),
            Int64Array a => a.GetValue(index)!.Value.ToString(invariant),
            UInt8Array a => a.GetValue(index)!.Value.ToString(invariant),
            UInt16Array a => a.GetValue(index)!.Value.ToString(invariant),
            UInt32Array a => a.GetValue(index)!.Value.ToString(invariant),
            UInt64Array a => a.GetValue(index)!.Value.ToString(invariant),
            StringArray a => a.GetString(index),
            BooleanArray a => a.GetValue(index)!.Value ? "true" : "false",
            FloatArray a when float.IsFinite(a.GetValue(index)!.Value) => a.GetValue(index)!.Value.ToString("R", invariant),
            DoubleArray a when double.IsFinite(a.GetValue(index)!.Value) => a.GetValue(index)!.Value.ToString("R", invariant),
            Decimal128Array a => DecimalText(a, index),
            Date32Array a => new DateOnly(1970, 1, 1).AddDays(a.GetValue(index)!.Value).ToString("yyyy-MM-dd", invariant),
            Date64Array a when a.GetValue(index)!.Value % 86400000 == 0 =>
                new DateOnly(1970, 1, 1).AddDays(checked((int)(a.GetValue(index)!.Value / 86400000)))
                    .ToString("yyyy-MM-dd", invariant),
            TimestampArray a => a.GetValue(index)!.Value.ToString(invariant),
            DictionaryArray a => ReadDictionary(a, index),
            _ => throw new HarnessException(FailureCode.UnsupportedType)
        };
    }

    private static string? ReadDictionary(DictionaryArray array, int index)
    {
        var key = ReadValue(array.Indices, index);
        if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var dictionaryIndex) ||
            dictionaryIndex < 0 || dictionaryIndex >= array.Dictionary.Length)
            throw new HarnessException(FailureCode.InvalidArrow);
        return ReadValue(array.Dictionary, dictionaryIndex);
    }

    private static string DecimalText(Decimal128Array array, int index)
    {
        var coefficient = new BigInteger(array.ValueBuffer.Span.Slice(checked((array.Offset + index) * 16), 16),
            isUnsigned: false, isBigEndian: false);
        var digits = BigInteger.Abs(coefficient).ToString(CultureInfo.InvariantCulture);
        var scale = array.Scale;
        var unsigned = scale <= 0
            ? digits + new string('0', -scale)
            : digits.PadLeft(scale + 1, '0').Insert(Math.Max(digits.Length, scale + 1) - scale, ".");
        return coefficient.Sign < 0 ? "-" + unsigned : unsigned;
    }

    private sealed class BudgetAllocator : MemoryAllocator
    {
        private int total;
        protected override IMemoryOwner<byte> AllocateInternal(int length, out int bytesAllocated)
        {
            var charged = checked(length + DefaultAlignment);
            total = checked(total + charged);
            if (total > HarnessContract.MaxArrowAllocationBytes)
                throw new HarnessException(FailureCode.LimitExceeded);
            bytesAllocated = charged;
            return Default.Value.Allocate(length);
        }
    }
}

// A small, bounded envelope reader, NOT a replacement FlatBuffers/Arrow implementation.
// Arrow accepts EOF in place of EOS and skips a mid-stream schema; this contract accepts neither.
internal static class ArrowFraming
{
    internal static List<(int Offset, int Length)> Split(byte[] payload)
    {
        var ranges = new List<(int, int)>();
        var position = 0;
        while (position < payload.Length)
        {
            if (ranges.Count >= 16) throw new HarnessException(FailureCode.LimitExceeded);
            var start = position;
            var hasSchema = false;
            var messages = 0;
            while (true)
            {
                if (++messages > 4096) throw new HarnessException(FailureCode.LimitExceeded);
                var prefix = Read32(payload, position);
                position = checked(position + 4);
                var metadataLength = prefix;
                if (prefix == -1)
                {
                    metadataLength = Read32(payload, position);
                    position = checked(position + 4);
                }
                if (metadataLength == 0)
                {
                    if (!hasSchema) throw new HarnessException(FailureCode.InvalidArrow);
                    ranges.Add((start, position - start));
                    break;
                }
                if (metadataLength < 8 || metadataLength > 1024 * 1024 ||
                    metadataLength > payload.Length - position)
                    throw new HarnessException(FailureCode.InvalidArrow);
                var metadata = payload.AsSpan(position, metadataLength);
                var table = Read32(metadata, 0);
                var headerOffset = Field(metadata, table, 1, 1);
                var header = headerOffset == 0 ? 0 : metadata[headerOffset];
                var bodyOffset = Field(metadata, table, 3, 8);
                var bodyLength = bodyOffset == 0 ? 0 : BinaryPrimitives.ReadInt64LittleEndian(metadata.Slice(bodyOffset, 8));
                var versionOffset = Field(metadata, table, 0, 2);
                var version = versionOffset == 0 ? 0 : BinaryPrimitives.ReadInt16LittleEndian(metadata.Slice(versionOffset, 2));
                if (version is not (3 or 4) || header is < 1 or > 3 || bodyLength < 0 ||
                    bodyLength > HarnessContract.MaxResponseBytes)
                    throw new HarnessException(FailureCode.InvalidArrow);
                if ((!hasSchema && header != 1) || (hasSchema && header == 1) || (header == 1 && bodyLength != 0))
                    throw new HarnessException(FailureCode.InvalidArrow);
                hasSchema = true;
                position = checked(position + metadataLength + (int)bodyLength);
                if (position > payload.Length) throw new HarnessException(FailureCode.InvalidArrow);
            }
        }
        return ranges;
    }

    private static int Field(ReadOnlySpan<byte> metadata, int table, int ordinal, int width)
    {
        var vtable = checked(table - Read32(metadata, table));
        if (vtable < 0 || vtable > metadata.Length - 4) throw new HarnessException(FailureCode.InvalidArrow);
        var vtableLength = BinaryPrimitives.ReadUInt16LittleEndian(metadata.Slice(vtable, 2));
        var objectLength = BinaryPrimitives.ReadUInt16LittleEndian(metadata.Slice(vtable + 2, 2));
        if (vtableLength < 4 || vtableLength % 2 != 0 || vtableLength > metadata.Length - vtable ||
            objectLength < 4 || objectLength > metadata.Length - table)
            throw new HarnessException(FailureCode.InvalidArrow);
        var slot = 4 + 2 * ordinal;
        if (slot + 2 > vtableLength) return 0;
        var offset = BinaryPrimitives.ReadUInt16LittleEndian(metadata.Slice(vtable + slot, 2));
        if (offset == 0) return 0;
        if (offset < 4 || offset > objectLength - width) throw new HarnessException(FailureCode.InvalidArrow);
        return checked(table + offset);
    }

    private static int Read32(ReadOnlySpan<byte> bytes, int position)
    {
        if (position < 0 || position > bytes.Length - 4) throw new HarnessException(FailureCode.InvalidArrow);
        return BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(position, 4));
    }
}
