using System.Globalization;
using Apache.Arrow;
using Apache.Arrow.Types;
using IqRls.Core;

namespace IqRls.Tests;

public sealed class ArrowTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void ReadsCompressedUncompressedAndLegacyFraming(bool compressed, bool legacy)
    {
        var result = ArrowResponseParser.Parse(TestData.Scalar(250, compressed, legacy));
        Assert.Equal("250", Assert.Single(Assert.Single(result.Rows)));
        Assert.Equal("int64", Assert.Single(result.Columns).ArrowType);
    }

    [Fact]
    public void ReadsAllBatchesInOneStream()
    {
        var payload = TestData.Arrow(new Schema([new Field("n", Int64Type.Default, false)], null),
            [[new Int64Array.Builder().Append(1).Build()], [new Int64Array.Builder().Append(2).Build()]]);
        var result = ArrowResponseParser.Parse(payload);
        Assert.Equal(["1", "2"], result.Rows.Select(r => r[0]));
    }

    [Fact]
    public void PreservesInt64DecimalDateTimestampAndNullWithoutFloatingPointCoercion()
    {
        var decimalType = new Decimal128Type(38, 4);
        var timestampType = new TimestampType(TimeUnit.Nanosecond, (string?)null);
        var schema = new Schema([
            new Field("Integer", Int64Type.Default, true),
            new Field("Currency", decimalType, true),
            new Field("Date", Date32Type.Default, true),
            new Field("Timestamp", timestampType, true),
            new Field("String", StringType.Default, true)
        ], null);
        var payload = TestData.Arrow(schema, [[
            new Int64Array.Builder().Append(long.MaxValue).AppendNull().Build(),
            new Decimal128Array.Builder(decimalType).Append("123456789012345678901234567890.1234").AppendNull().Build(),
            new Date32Array.Builder().Append(new DateTime(2026, 9, 28)).AppendNull().Build(),
            new TimestampArray(timestampType, new ArrowBuffer.Builder<long>().Append(1234567890123456789L).Append(0).Build(),
                new ArrowBuffer(new byte[] { 1 }), 2, 1, 0),
            new StringArray.Builder().Append("synthetic").AppendNull().Build()
        ]]);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            var result = ArrowResponseParser.Parse(payload);
            Assert.Equal(new string?[] { "9223372036854775807", "123456789012345678901234567890.1234", "2026-09-28",
                "1234567890123456789", "synthetic" }, result.Rows[0].ToArray());
            Assert.All(result.Rows[1], Assert.Null);
            Assert.Equal("decimal128(38,4)", result.Columns[1].ArrowType);
            Assert.Contains("Nanosecond", result.Columns[3].ArrowType);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("-0.1234")]
    [InlineData("0.0000")]
    [InlineData("-922337203685477.5808")]
    public void PreservesSignedCurrencyAndScale(string number)
    {
        var type = new Decimal128Type(19, 4);
        var result = ArrowResponseParser.Parse(TestData.Arrow(new Schema([new Field("amount", type, false)], null),
            [[new Decimal128Array.Builder(type).Append(number).Build()]]));
        Assert.Equal(number, result.Rows[0][0]);
    }

    [Fact]
    public void ErrorRowsetAloneIsNotSuccess()
    {
        var error = Assert.Throws<HarnessException>(() => ArrowResponseParser.Parse(TestData.Error()));
        Assert.Equal(FailureCode.QueryError, error.Code);
        Assert.DoesNotContain("SENSITIVE", error.ToString());
    }

    [Fact]
    public void ReadsErrorStreamAfterDataAndRejectsAllPartialRows()
    {
        var payload = TestData.Scalar().Concat(TestData.Error()).ToArray();
        Assert.Equal(FailureCode.QueryError, Assert.Throws<HarnessException>(() => ArrowResponseParser.Parse(payload)).Code);
    }

    [Fact]
    public void MultipleDataResultSetsAreOutsideTheToolContract()
    {
        var payload = TestData.Scalar().Concat(TestData.Scalar(700)).ToArray();
        Assert.Equal(FailureCode.UnexpectedResultSets,
            Assert.Throws<HarnessException>(() => ArrowResponseParser.Parse(payload)).Code);
    }

    [Fact]
    public void EveryTruncationOfAValidPayloadFails()
    {
        var payload = TestData.Scalar();
        for (var length = 0; length < payload.Length; length++)
        {
            var exception = Assert.Throws<HarnessException>(() => ArrowResponseParser.Parse(payload[..length]));
            Assert.Equal(FailureCode.InvalidArrow, exception.Code);
        }
    }

    [Fact]
    public void MissingEosBeforeAnotherSchemaCannotMergeStreams()
    {
        var first = TestData.Scalar();
        var invalid = first[..^8].Concat(TestData.Scalar(700)).ToArray();
        Assert.Equal(FailureCode.InvalidArrow,
            Assert.Throws<HarnessException>(() => ArrowResponseParser.Parse(invalid)).Code);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(17)]
    public void JunkAfterValidStreamNeverBecomesPartialSuccess(int count)
    {
        var payload = TestData.Scalar().Concat(Enumerable.Repeat((byte)0x7f, count)).ToArray();
        Assert.Equal(FailureCode.InvalidArrow,
            Assert.Throws<HarnessException>(() => ArrowResponseParser.Parse(payload)).Code);
    }

    [Fact]
    public void HugeMetadataLengthIsRejectedBeforeAllocation()
    {
        byte[] invalid = [255, 255, 255, 255, 255, 255, 255, 127];
        Assert.Equal(FailureCode.InvalidArrow,
            Assert.Throws<HarnessException>(() => ArrowResponseParser.Parse(invalid)).Code);
    }

    [Fact]
    public void EmptySchemaStreamWithColumnsIsValidZeroRows()
    {
        var payload = TestData.Arrow(new Schema([new Field("n", Int64Type.Default, true)], null), []);
        var result = ArrowResponseParser.Parse(payload);
        Assert.Empty(result.Rows);
        Assert.Single(result.Columns);
    }

    [Fact]
    public void EosWithoutSchemaIsInvalid()
    {
        Assert.Equal(FailureCode.InvalidArrow,
            Assert.Throws<HarnessException>(() => ArrowResponseParser.Parse([255, 255, 255, 255, 0, 0, 0, 0])).Code);
    }

    [Fact]
    public void OversizedResponseFails()
    {
        Assert.Equal(FailureCode.LimitExceeded,
            Assert.Throws<HarnessException>(() => ArrowResponseParser.Parse(new byte[HarnessContract.MaxResponseBytes + 1])).Code);
    }

    [Fact]
    public void AtRowCapFailsBecauseTruncationCannotBeExcluded()
    {
        var payload = TestData.Arrow(new Schema([new Field("n", Int64Type.Default, false)], null),
            [[new Int64Array.Builder().AppendRange(Enumerable.Repeat(1L, 1000)).Build()]]);
        Assert.Equal(FailureCode.LimitExceeded, Assert.Throws<HarnessException>(() => ArrowResponseParser.Parse(payload)).Code);
    }

    [Fact]
    public void OversizedCellFailsWithoutReturningRows()
    {
        var payload = TestData.Arrow(new Schema([new Field("n", StringType.Default, false)], null),
            [[new StringArray.Builder().Append(new string('a', HarnessContract.MaxCellCharacters + 1)).Build()]]);
        Assert.Equal(FailureCode.LimitExceeded, Assert.Throws<HarnessException>(() => ArrowResponseParser.Parse(payload)).Code);
    }

    [Fact]
    public void OversizedDecompressedAllocationFails()
    {
        var payload = TestData.Arrow(new Schema([new Field("n", StringType.Default, false)], null),
            [[new StringArray.Builder().Append(new string('a', HarnessContract.MaxArrowAllocationBytes + 1)).Build()]]);
        Assert.True(payload.Length < HarnessContract.MaxResponseBytes);
        Assert.Equal(FailureCode.LimitExceeded, Assert.Throws<HarnessException>(() => ArrowResponseParser.Parse(payload)).Code);
    }

    [Fact]
    public void InvalidIsErrorMetadataIsNotAssumedToBeData()
    {
        var payload = TestData.Arrow(new Schema([new Field("n", Int64Type.Default, true)],
            new Dictionary<string, string> { ["IsError"] = "maybe" }), []);
        Assert.Equal(FailureCode.InvalidArrow, Assert.Throws<HarnessException>(() => ArrowResponseParser.Parse(payload)).Code);
    }

    [Fact]
    public void UnknownTypesFailEvenWithNoRows()
    {
        var payload = TestData.Arrow(new Schema([new Field("binary", BinaryType.Default, true)], null), []);
        Assert.Equal(FailureCode.UnsupportedType, Assert.Throws<HarnessException>(() => ArrowResponseParser.Parse(payload)).Code);
    }

    [Fact]
    public void MalformedLz4FrameIsRejectedRatherThanReturningData()
    {
        var payload = TestData.Scalar();
        var magic = new byte[] { 0x04, 0x22, 0x4D, 0x18 };
        var index = payload.AsSpan().IndexOf(magic);
        Assert.True(index >= 0, "Test data must contain a real LZ4_FRAME buffer.");
        payload[index] ^= 0x7f;
        Assert.Equal(FailureCode.InvalidArrow, Assert.Throws<HarnessException>(() => ArrowResponseParser.Parse(payload)).Code);
    }

    [Fact]
    public void ExcessiveCumulativeOutputIsRejected()
    {
        var payload = TestData.Arrow(new Schema([new Field("n", StringType.Default, false)], null),
        [[new StringArray.Builder().AppendRange(Enumerable.Repeat(new string('a', 16384), 129)).Build()]]);
        Assert.Equal(FailureCode.LimitExceeded, Assert.Throws<HarnessException>(() => ArrowResponseParser.Parse(payload)).Code);
    }
}
