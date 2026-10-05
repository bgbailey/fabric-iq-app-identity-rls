using Apache.Arrow;
using Apache.Arrow.Types;
using SemanticGateway.Fabric;

namespace IqRls.Tests;

public sealed class ProductionArrowTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void ReadsCompressedUncompressedAndLegacyFraming(bool compressed, bool legacy)
    {
        var result = ArrowResults.Parse(TestData.Scalar(250, compressed, legacy));

        Assert.Equal("250", Assert.Single(Assert.Single(result.Rows)));
        Assert.Equal(new ResultColumn("Total", "integer"), Assert.Single(result.Columns));
    }

    [Fact]
    public void ReadsAllBatchesInOneStream()
    {
        var payload = TestData.Arrow(new Schema([new Field("Total", Int64Type.Default, false)], null),
            [[new Int64Array.Builder().Append(250).Build()], [new Int64Array.Builder().Append(700).Build()]]);

        var result = ArrowResults.Parse(payload);

        Assert.Equal(new[] { "250", "700" }, result.Rows.Select(row => Assert.Single(row)));
        Assert.Equal(new ResultColumn("Total", "integer"), Assert.Single(result.Columns));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RejectsMultipleCompleteDataStreams(bool firstCompressed, bool secondCompressed)
    {
        var payload = TestData.Scalar(250, firstCompressed).Concat(TestData.Scalar(700, secondCompressed)).ToArray();

        var error = Assert.Throws<ToolException>(() => ArrowResults.Parse(payload));

        Assert.Equal("The query returned multiple data result sets; only one is supported.", error.Message);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void RejectsEmptyAndDataStreamCombinations(bool firstEmpty, bool secondEmpty, bool zeroRowBatch)
    {
        var first = firstEmpty ? EmptyStream(zeroRowBatch) : TestData.Scalar(250);
        var second = secondEmpty ? EmptyStream(zeroRowBatch) : TestData.Scalar(700);

        var error = Assert.Throws<ToolException>(() => ArrowResults.Parse(first.Concat(second).ToArray()));

        Assert.Equal("The query returned multiple data result sets; only one is supported.", error.Message);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void ReadsSchemaBearingEmptyResults(bool zeroRowBatch, bool compressed)
    {
        var result = ArrowResults.Parse(EmptyStream(zeroRowBatch, compressed));

        Assert.Empty(result.Rows);
        Assert.Equal(new ResultColumn("Total", "integer"), Assert.Single(result.Columns));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ErrorStreamsRejectAllData(bool prependData)
    {
        var payload = prependData ? TestData.Scalar().Concat(TestData.Error()).ToArray() : TestData.Error();

        var error = Assert.Throws<ToolException>(() => ArrowResults.Parse(payload));

        Assert.Equal("The DAX query failed: SENSITIVE_FAULT_SENTINEL", error.Message);
    }

    [Theory]
    [InlineData("metadata fault", "row message", "metadata fault")]
    [InlineData("", "row message", "")]
    [InlineData(null, "row message", "row message")]
    [InlineData(null, null, "unknown error")]
    public void PreservesFaultMetadataPriorityAndMessageFallback(string? fault, string? rowMessage, string expected)
    {
        var metadata = new Dictionary<string, string> { ["iSeRrOr"] = "TRUE" };
        if (fault is not null)
            metadata["FaultString"] = fault;
        var messages = new StringArray.Builder();
        if (rowMessage is null)
            messages.AppendNull();
        else
            messages.Append(rowMessage);
        var payload = TestData.Arrow(new Schema([new Field("ErrorMessage", StringType.Default, true)], metadata),
            [[messages.Build()]]);

        var error = Assert.Throws<ToolException>(() => ArrowResults.Parse(payload));

        Assert.Equal("The DAX query failed: " + expected, error.Message);
    }

    [Fact]
    public void EmptyPayloadPreservesNoResultError()
    {
        var error = Assert.Throws<ToolException>(() => ArrowResults.Parse([]));

        Assert.Equal("The query returned no result set.", error.Message);
    }

    private static byte[] EmptyStream(bool zeroRowBatch, bool compressed = true)
    {
        IArrowArray[][] batches = zeroRowBatch ? [[new Int64Array.Builder().Build()]] : [];
        return TestData.Arrow(new Schema([new Field("Total", Int64Type.Default, true)], null), batches, compressed);
    }
}
