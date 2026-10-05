using SemanticGateway.Chat;
using SemanticGateway.Fabric;
using SemanticGateway.Tools;

namespace IqRls.Tests;

public sealed class ChatTableSelectionTests
{
    private const string Query = "EVALUATE ROW(\"Value\", 1)";

    [Fact]
    public void LookupsOnlyLeaveSelectionNull()
    {
        QueryResult? selected = null;
        var lookup = Table("lookup");
        var empty = new QueryResult(lookup.Columns, []);

        selected = ChatAgent.SelectAnalyticalTable(selected, SemanticModelTools.SearchValues,
            new ToolResult("match", Table: lookup, Dax: Query));
        Assert.Null(selected);
        selected = ChatAgent.SelectAnalyticalTable(selected, SemanticModelTools.SearchValues,
            new ToolResult("no matches", Table: empty, Dax: Query));
        Assert.Null(selected);
        selected = ChatAgent.SelectAnalyticalTable(selected, SemanticModelTools.SearchValues,
            new ToolResult("lookup failed", IsError: true, Table: lookup, Dax: Query));
        Assert.Null(selected);
        selected = ChatAgent.SelectAnalyticalTable(selected, SemanticModelTools.GetSchema,
            new ToolResult("schema", Table: lookup));
        Assert.Null(selected);
    }

    [Theory]
    [InlineData(SemanticModelTools.SearchValues, false, false)]
    [InlineData(SemanticModelTools.SearchValues, false, true)]
    [InlineData(SemanticModelTools.SearchValues, true, false)]
    [InlineData(SemanticModelTools.GetSchema, false, false)]
    [InlineData(SemanticModelTools.GetSchema, false, true)]
    [InlineData(SemanticModelTools.GetSchema, true, false)]
    public void LookupRetainsAnalysis(string toolName, bool isError, bool empty)
    {
        var analysis = Table("A");
        var lookup = Table("lookup");
        var lookupTable = empty ? new QueryResult(lookup.Columns, []) : lookup;
        var selected = ChatAgent.SelectAnalyticalTable(null, SemanticModelTools.ExecuteDax,
            new ToolResult("analysis", Table: analysis, Dax: Query));
        Assert.Same(analysis, selected);

        selected = ChatAgent.SelectAnalyticalTable(selected, toolName,
            new ToolResult("lookup", IsError: isError, Table: lookupTable, Dax: Query));
        Assert.Same(analysis, selected);
        selected = ChatAgent.SelectAnalyticalTable(selected, toolName,
            new ToolResult("no table", IsError: isError));
        Assert.Same(analysis, selected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedAnalysisClearsSelection(bool hasPartialTable)
    {
        var analysis = Table("A");
        var selected = ChatAgent.SelectAnalyticalTable(null, SemanticModelTools.ExecuteDax,
            new ToolResult("analysis", Table: analysis, Dax: Query));
        Assert.Same(analysis, selected);

        selected = ChatAgent.SelectAnalyticalTable(selected, SemanticModelTools.ExecuteDax,
            new ToolResult("analysis failed", IsError: true,
                Table: hasPartialTable ? Table("partial B") : null, Dax: Query));
        Assert.Null(selected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LookupAfterFailureStaysNull(bool lookupIsError)
    {
        var analysis = Table("A");
        var selected = ChatAgent.SelectAnalyticalTable(null, SemanticModelTools.ExecuteDax,
            new ToolResult("analysis", Table: analysis, Dax: Query));
        selected = ChatAgent.SelectAnalyticalTable(selected, SemanticModelTools.ExecuteDax,
            new ToolResult("analysis failed", IsError: true, Table: Table("partial B"), Dax: Query));
        Assert.Null(selected);

        selected = ChatAgent.SelectAnalyticalTable(selected, SemanticModelTools.SearchValues,
            new ToolResult("lookup", IsError: lookupIsError, Table: analysis, Dax: Query));
        Assert.Null(selected);
    }

    [Fact]
    public void RecoverySelectsNewTable()
    {
        var selected = ChatAgent.SelectAnalyticalTable(null, SemanticModelTools.ExecuteDax,
            new ToolResult("analysis failed", IsError: true, Dax: Query));
        Assert.Null(selected);
        var recovered = Table("B");

        selected = ChatAgent.SelectAnalyticalTable(selected, SemanticModelTools.ExecuteDax,
            new ToolResult("recovered", Table: recovered, Dax: Query));
        Assert.Same(recovered, selected);
    }

    [Fact]
    public void EmptyAnalysisPreservesSchema()
    {
        var analysis = Table("A");
        ResultColumn[] columns = [new("Category", "String"), new("Total", "Int64")];
        var empty = new QueryResult(columns, []);
        var selected = ChatAgent.SelectAnalyticalTable(null, SemanticModelTools.ExecuteDax,
            new ToolResult("analysis", Table: analysis, Dax: Query));

        selected = ChatAgent.SelectAnalyticalTable(selected, SemanticModelTools.ExecuteDax,
            new ToolResult("no rows", Table: empty, Dax: Query));
        Assert.Same(empty, selected);
        Assert.Same(columns, selected!.Columns);
        Assert.Collection(selected.Columns,
            column => Assert.Equal(new ResultColumn("Category", "String"), column),
            column => Assert.Equal(new ResultColumn("Total", "Int64"), column));
        Assert.Empty(selected.Rows);
        selected = ChatAgent.SelectAnalyticalTable(selected, SemanticModelTools.SearchValues,
            new ToolResult("lookup", Table: Table("lookup")));
        Assert.Same(empty, selected);
    }

    [Fact]
    public void SuccessWithoutTableClearsSelection()
    {
        var analysis = Table("A");
        var selected = ChatAgent.SelectAnalyticalTable(null, SemanticModelTools.ExecuteDax,
            new ToolResult("analysis", Table: analysis, Dax: Query));
        Assert.Same(analysis, selected);

        selected = ChatAgent.SelectAnalyticalTable(selected, SemanticModelTools.ExecuteDax,
            new ToolResult("successful without table", Dax: Query));
        Assert.Null(selected);
    }

    [Fact]
    public void MultipleAnalysesFollowExecutionOrder()
    {
        var a = Table("A");
        var b = Table("B");
        var c = Table("C");
        var selected = ChatAgent.SelectAnalyticalTable(null, SemanticModelTools.ExecuteDax,
            new ToolResult("first", Table: a, Dax: "EVALUATE ROW(\"Value\", 1)"));
        Assert.Same(a, selected);
        selected = ChatAgent.SelectAnalyticalTable(selected, SemanticModelTools.ExecuteDax,
            new ToolResult("second", Table: b, Dax: "EVALUATE ROW(\"Value\", 2)"));
        Assert.Same(b, selected);
        selected = ChatAgent.SelectAnalyticalTable(selected, SemanticModelTools.SearchValues,
            new ToolResult("lookup", Table: a));
        Assert.Same(b, selected);
        selected = ChatAgent.SelectAnalyticalTable(selected, SemanticModelTools.ExecuteDax,
            new ToolResult("third", Table: c, Dax: "EVALUATE ROW(\"Value\", 3)"));
        Assert.Same(c, selected);
    }

    [Fact]
    public void RepeatedQuerySuccessWins()
    {
        var first = new ToolResult("first", Table: Table("A"), Dax: Query);
        var second = new ToolResult("second", Table: Table("B"), Dax: Query);
        Assert.Equal(first.Dax, second.Dax);
        var selected = ChatAgent.SelectAnalyticalTable(null, SemanticModelTools.ExecuteDax, first);
        Assert.Same(first.Table, selected);

        selected = ChatAgent.SelectAnalyticalTable(selected, SemanticModelTools.ExecuteDax, second);
        Assert.Same(second.Table, selected);
    }

    [Fact]
    public void RepeatedQueryFailureClears()
    {
        var first = new ToolResult("first", Table: Table("A"), Dax: Query);
        var second = new ToolResult("second failed", IsError: true, Table: Table("partial B"), Dax: Query);
        Assert.Equal(first.Dax, second.Dax);
        var selected = ChatAgent.SelectAnalyticalTable(null, SemanticModelTools.ExecuteDax, first);
        Assert.Same(first.Table, selected);

        selected = ChatAgent.SelectAnalyticalTable(selected, SemanticModelTools.ExecuteDax, second);
        Assert.Null(selected);
    }

    [Fact]
    public void SeparateRequestsStayIndependent()
    {
        QueryResult? firstRequest = null;
        QueryResult? secondRequest = null;
        var a = Table("A");
        var b = Table("B");
        firstRequest = ChatAgent.SelectAnalyticalTable(firstRequest, SemanticModelTools.ExecuteDax,
            new ToolResult("first request", Table: a, Dax: Query));
        Assert.Same(a, firstRequest);
        Assert.Null(secondRequest);

        secondRequest = ChatAgent.SelectAnalyticalTable(secondRequest, SemanticModelTools.SearchValues,
            new ToolResult("second request lookup", Table: a));
        Assert.Null(secondRequest);
        secondRequest = ChatAgent.SelectAnalyticalTable(secondRequest, SemanticModelTools.ExecuteDax,
            new ToolResult("second request analysis", Table: b, Dax: Query));
        Assert.Same(b, secondRequest);
        Assert.Same(a, firstRequest);
    }

    [Theory]
    [InlineData("EXECUTE_DAX", false)]
    [InlineData("EXECUTE_DAX", true)]
    [InlineData("execute_dax ", false)]
    [InlineData(" execute_dax", true)]
    [InlineData("execute_dax_extra", false)]
    [InlineData("unknown", true)]
    public void NonExactToolNamesRetainAnalysis(string toolName, bool isError)
    {
        var analysis = Table("A");
        var selected = ChatAgent.SelectAnalyticalTable(null, SemanticModelTools.ExecuteDax,
            new ToolResult("analysis", Table: analysis, Dax: Query));

        selected = ChatAgent.SelectAnalyticalTable(selected, toolName,
            new ToolResult("other tool", IsError: isError, Table: Table("B"), Dax: Query));
        Assert.Same(analysis, selected);
    }

    private static QueryResult Table(string value) =>
        new([new ResultColumn("Value", "String")], [[value]]);
}
