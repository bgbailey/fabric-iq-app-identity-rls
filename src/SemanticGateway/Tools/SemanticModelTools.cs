using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SemanticGateway.Fabric;
using SemanticGateway.Identity;

namespace SemanticGateway.Tools;

public sealed record ToolDefinition(string Name, string Description, string InputSchema);

public sealed record ToolResult(string Text, bool IsError = false, QueryResult? Table = null, string? Dax = null);

/// <summary>
/// The three tools the gateway offers, to its own chat agent and to any MCP client.
///
///   get_semantic_model_schema  schema from Fabric IQ (cached; identical for every user)
///   search_values              find exact text values, only among rows this user may see
///   execute_dax                run a read-only DAX query under this user's row-level security
///
/// The user is always passed in by the caller from the validated token. No tool accepts a user,
/// role or key as an argument.
/// </summary>
public sealed class SemanticModelTools(SchemaCache schemaCache, DaxQueryClient dax, IOptions<FabricIqSettings> iqOptions)
{
    public const string GetSchema = "get_semantic_model_schema";
    public const string SearchValues = "search_values";
    public const string ExecuteDax = "execute_dax";

    private const int MaxRowsForModel = 200;

    private const string Instructions =
        "Row-level security for the signed-in user is applied automatically to every query. " +
        "Never filter by user, role or security tables. Prefer existing measures. " +
        "Use search_values to find exact text values before filtering on them. " +
        "Write one read-only DAX query that starts with EVALUATE (or DEFINE ... EVALUATE) and add ORDER BY for multi-row results.";

    public static IReadOnlyList<ToolDefinition> Definitions { get; } =
    [
        new(GetSchema,
            "Get the semantic model schema (tables, columns, measures, relationships) to write DAX against. Call this first.",
            """{"type":"object","properties":{},"additionalProperties":false}"""),
        new(SearchValues,
            "Find text values in one column that contain the search text, only among rows the signed-in user may see. Use before filtering on a text value.",
            """{"type":"object","properties":{"table":{"type":"string","description":"Table name from the schema."},"column":{"type":"string","description":"Text column name from the schema."},"search_text":{"type":"string","description":"Text to look for."}},"required":["table","column","search_text"],"additionalProperties":false}"""),
        new(ExecuteDax,
            "Run one read-only DAX query against the semantic model. Row-level security for the signed-in user is applied automatically.",
            """{"type":"object","properties":{"query":{"type":"string","description":"A DAX query starting with EVALUATE or DEFINE."}},"required":["query"],"additionalProperties":false}""")
    ];

    public async Task<ToolResult> InvokeAsync(AppUser user, string tool, IDictionary<string, JsonElement>? arguments, CancellationToken cancellationToken)
    {
        try
        {
            string Argument(string name) => arguments is not null && arguments.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()!
                : throw new ToolException($"Missing argument '{name}'.");

            return tool switch
            {
                GetSchema => await GetSchemaAsync(cancellationToken),
                SearchValues => await SearchValuesAsync(user, Argument("table"), Argument("column"), Argument("search_text"), cancellationToken),
                ExecuteDax => await ExecuteDaxAsync(user, Argument("query"), cancellationToken),
                _ => throw new ToolException($"Unknown tool '{tool}'.")
            };
        }
        catch (ToolException error)
        {
            return new ToolResult(JsonSerializer.Serialize(new { error = error.Message }), IsError: true);
        }
    }

    private async Task<ToolResult> GetSchemaAsync(CancellationToken cancellationToken)
    {
        var schema = await schemaCache.GetAsync(cancellationToken);
        return new ToolResult(JsonSerializer.Serialize(new { source = schema.Source, retrievedAtUtc = schema.RetrievedAtUtc, instructions = Instructions, schema = schema.Schema }));
    }

    private async Task<ToolResult> SearchValuesAsync(AppUser user, string table, string column, string searchText, CancellationToken cancellationToken)
    {
        // Only allow columns that appear in the schema the model was given.
        var schema = await schemaCache.GetAsync(cancellationToken);
        var known = schema.Schema["Tables"]?.AsArray().FirstOrDefault(t => string.Equals((string?)t?["Name"], table, StringComparison.OrdinalIgnoreCase))
            ?["Columns"]?.AsArray().Any(c => string.Equals((string?)c?["Name"], column, StringComparison.OrdinalIgnoreCase)) == true;
        if (!known) throw new ToolException($"'{table}'[{column}] is not a column in the schema.");

        var reference = $"'{table.Replace("'", "''")}'[{column.Replace("]", "]]")}]";
        var query = $"EVALUATE TOPN(25, FILTER(VALUES({reference}), CONTAINSSTRING({reference}, \"{searchText.Replace("\"", "\"\"")}\")), {reference}, ASC) ORDER BY {reference}";
        var result = await dax.ExecuteAsync(user, query, cancellationToken);
        var values = result.Rows.Select(row => row[0]).Where(value => value is not null).ToArray();
        if (values.Length > 0)
            return new ToolResult(JsonSerializer.Serialize(new { table, column, values }), Table: result, Dax: query);

        // No match: return the values this user can see in the column (still under RLS), so the
        // model can map wording like "Customer B" to the stored value "B".
        var visibleQuery = $"EVALUATE TOPN(25, VALUES({reference}), {reference}, ASC) ORDER BY {reference}";
        var visible = await dax.ExecuteAsync(user, visibleQuery, cancellationToken);
        var visibleValues = visible.Rows.Select(row => row[0]).Where(value => value is not null).ToArray();
        var note = visibleValues.Length == 0
            ? "This user can see no values in this column."
            : "No value contains the search text. These are the values this user can see; use one of them if it matches the question, otherwise the value is not visible to this user.";
        return new ToolResult(JsonSerializer.Serialize(new { table, column, values = Array.Empty<string>(), visibleValues, note }), Table: visible, Dax: visibleQuery);
    }

    private async Task<ToolResult> ExecuteDaxAsync(AppUser user, string query, CancellationToken cancellationToken)
    {
        CheckQuery(query);
        var result = await dax.ExecuteAsync(user, query, cancellationToken);
        var text = JsonSerializer.Serialize(new
        {
            columns = result.Columns.Select(c => c.Name),
            rows = result.Rows.Take(MaxRowsForModel),
            rowCount = result.Rows.Count,
            truncated = result.Rows.Count > MaxRowsForModel
        });
        return new ToolResult(text, Table: result, Dax: query);
    }

    // Defense in depth only: the RLS role in the model is the security boundary. This stops the
    // model from probing identity functions, DMVs or excluded tables, which analytics never needs.
    private void CheckQuery(string query)
    {
        if (!Regex.IsMatch(query, @"\bEVALUATE\b", RegexOptions.IgnoreCase))
            throw new ToolException("The query must be a DAX query that contains EVALUATE.");
        if (Regex.IsMatch(query, @"\b(CUSTOMDATA|USERNAME|USERPRINCIPALNAME|USEROBJECTID|INFO\.\w+)\s*\(|\$SYSTEM\.", RegexOptions.IgnoreCase))
            throw new ToolException("Identity functions, INFO functions and DMVs are not available.");
        if (iqOptions.Value.ExcludeTables.Any(table => query.Contains($"'{table}'", StringComparison.OrdinalIgnoreCase)))
            throw new ToolException("The query references a table that is not part of the schema.");
    }
}
