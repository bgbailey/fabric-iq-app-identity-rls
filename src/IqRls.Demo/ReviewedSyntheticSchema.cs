using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IqRls.Demo;

public sealed class ReviewedSyntheticSchema
{
    private static readonly Dictionary<string, Dictionary<string, string>> Expected = new(StringComparer.Ordinal)
    {
        ["Activity"] = new(StringComparer.Ordinal) { ["Activity Key"] = "int64", ["Scope Key"] = "int64", ["Date Key"] = "int64", ["Amount"] = "decimal" },
        ["Scope"] = new(StringComparer.Ordinal) { ["Scope Key"] = "int64", ["Customer"] = "string", ["Product"] = "string" },
        ["Date"] = new(StringComparer.Ordinal) { ["Date Key"] = "int64", ["Date"] = "dateTime", ["Year"] = "int64", ["Month"] = "int64", ["Day"] = "int64" }
    };
    public string Json { get; }
    public string Hash { get; }
    private ReviewedSyntheticSchema(string json)
    {
        Json = json;
        Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    public static ReviewedSyntheticSchema FromToolResult(JsonElement result, Guid? expectedArtifactId = null)
    {
        try
        {
            AssertUnique(result);
            CheckCompleteness(result);
            if (Try(result, "isError", out var error) && error.ValueKind != JsonValueKind.False)
                throw new DemoException("iq_tool_failure", "The live IQ schema tool did not succeed. No raw tool details are exposed.", 502);
            ReviewedSyntheticSchema? structured = null;
            Guid? citationId = null;
            if (Try(result, "structuredContent", out var structuredContent))
            {
                CheckCompleteness(structuredContent);
                if (Try(structuredContent, "artifact_citation", out var citation))
                {
                    // Live IQ returns citation metadata here and the actual schema in content[].text.
                    if (structuredContent.EnumerateObject().Count() != 1) throw Failure();
                    citationId = ArtifactId(citation);
                }
                else structured = Project(structuredContent);
            }
            ReviewedSyntheticSchema? text = null;
            Guid? textArtifactId = null;
            if (Try(result, "content", out var content))
            {
                if (content.ValueKind != JsonValueKind.Array || content.GetArrayLength() != 1) throw Failure();
                var item = content[0];
                if (Field(item, "type").GetString() != "text" || Field(item, "text").ValueKind != JsonValueKind.String)
                    throw Failure();
                var json = Field(item, "text").GetString()!;
                if (Encoding.UTF8.GetByteCount(json) > FabricIqSchemaClient.MaxResponseBytes) throw Failure();
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
                if (Try(document.RootElement, "semanticModel", out var model))
                    textArtifactId = ArtifactId(model);
                text = Project(document.RootElement);
            }
            if ((citationId is not null && textArtifactId is not null && citationId != textArtifactId) ||
                (expectedArtifactId is not null &&
                 ((citationId is not null && citationId != expectedArtifactId) ||
                  (textArtifactId is not null && textArtifactId != expectedArtifactId))))
                throw Failure();
            if (structured is not null && text is not null && structured.Hash != text.Hash) throw Failure();
            return structured ?? text ?? throw Failure();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw Failure();
        }
    }

    private static ReviewedSyntheticSchema Project(JsonElement data)
    {
        AssertUnique(data);
        CheckCompleteness(data);
        var schema = Try(data, "schema", out var nested) ? nested : data;
        CheckCompleteness(schema);
        var tables = Field(schema, "Tables");
        if (tables.ValueKind != JsonValueKind.Array || tables.GetArrayLength() is < 3 or > 4) throw Failure();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var projected = new List<object>();
        foreach (var table in tables.EnumerateArray())
        {
            CheckCompleteness(table);
            var name = Name(table);
            if (!seen.Add(name)) throw Failure();
            if (name == "User Access") continue;
            if (!Expected.TryGetValue(name, out var expected)) throw Failure();
            var columns = Field(table, "Columns");
            if (columns.ValueKind != JsonValueKind.Array ||
                (columns.GetArrayLength() != expected.Count && !(name == "Activity" && columns.GetArrayLength() == 2)))
                throw Failure();
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var column in columns.EnumerateArray())
            {
                CheckCompleteness(column);
                var columnName = Name(column);
                if (Try(column, "Type", out _) && Try(column, "DataType", out _)) throw Failure();
                var kind = Try(column, "Type", out var columnType) ? columnType : Field(column, "DataType");
                var type = kind.GetString()?.ToLowerInvariant() switch
                {
                    "int64" or "integer" or "whole number" => "int64",
                    "decimal" or "currency" or "fixed decimal number" => "decimal",
                    "string" or "text" => "string",
                    "datetime" or "date" or "date/time" => "dateTime",
                    _ => throw Failure()
                };
                if (!expected.TryGetValue(columnName, out var want) || type != want || !found.TryAdd(columnName, type))
                    throw Failure();
            }
            // IQ omits hidden nonrelationship fact columns; never synthesize them into the live projection.
            if (name == "Activity" && (!found.ContainsKey("Scope Key") || !found.ContainsKey("Date Key")))
                throw Failure();
            var measures = new HashSet<string>(StringComparer.Ordinal);
            if (Try(table, "Measures", out var rawMeasures))
            {
                if (rawMeasures.ValueKind != JsonValueKind.Array || rawMeasures.GetArrayLength() > 2) throw Failure();
                foreach (var measure in rawMeasures.EnumerateArray())
                {
                    CheckCompleteness(measure);
                    var measureName = Name(measure);
                    if (name != "Activity" || measureName is not "Total Amount" and not "Activity Count" || !measures.Add(measureName))
                        throw Failure();
                }
            }
            if (name == "Activity" && measures.Count != 2) throw Failure();
            projected.Add(new { name, columns = found.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new { name = p.Key, type = p.Value }).ToArray(), measures = measures.Order(StringComparer.Ordinal).ToArray() });
        }
        if (Expected.Keys.Any(n => !seen.Contains(n))) throw Failure();

        if (Try(schema, "ActiveRelationships", out _) && Try(schema, "Relationships", out _)) throw Failure();
        var relationships = Try(schema, "ActiveRelationships", out var active) ? active : Field(schema, "Relationships");
        if (relationships.ValueKind != JsonValueKind.Array || relationships.GetArrayLength() != 2) throw Failure();
        var pairs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var relationship in relationships.EnumerateArray())
        {
            CheckCompleteness(relationship);
            if (Try(relationship, "IsActive", out var isActive) && isActive.ValueKind != JsonValueKind.True) throw Failure();
            string from;
            string to;
            if (Try(relationship, "FK", out var fk) && Try(relationship, "PK", out var pk))
            {
                from = Reference(fk);
                to = Reference(pk);
            }
            else
            {
                from = ColumnReference(Field(relationship, "FromTable").GetString()!, Field(relationship, "FromColumn").GetString()!);
                to = ColumnReference(Field(relationship, "ToTable").GetString()!, Field(relationship, "ToColumn").GetString()!);
            }
            if (from + "->" + to is not "Activity[Scope Key]->Scope[Scope Key]" and not "Activity[Date Key]->Date[Date Key]" ||
                !pairs.Add(from + "->" + to)) throw Failure();
            if (Try(relationship, "CrossFilteringBehavior", out var filtering) &&
                filtering.GetString() is not "OneDirection" and not "oneDirection") throw Failure();
            if (Try(relationship, "UnidirectionalFilter", out var direction))
            {
                var parent = to[..to.IndexOf('[')];
                if (direction.GetString() != $"'{parent}' filters 'Activity'") throw Failure();
            }
        }
        return new ReviewedSyntheticSchema(JsonSerializer.Serialize(new
        {
            tables = projected.OrderBy(t => JsonSerializer.Serialize(t), StringComparer.Ordinal).ToArray(),
            relationships = pairs.Order(StringComparer.Ordinal).ToArray()
        }));
    }

    private static Guid ArtifactId(JsonElement citation) =>
        Guid.TryParseExact(Field(citation, "ArtifactId").GetString(), "D", out var id) &&
        id != Guid.Empty ? id : throw Failure();

    private static string Reference(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            return ColumnReference(Field(value, "Table").GetString()!, Field(value, "Column").GetString()!);
        var text = value.GetString() ?? throw Failure();
        var match = Regex.Match(text, @"\A(?:(Activity|Scope|Date)|'(Activity|Scope|Date)')\[(Activity Key|Scope Key|Date Key|Amount|Customer|Product|Date|Year|Month|Day)\]\z",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        if (!match.Success) throw Failure();
        return ColumnReference(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value, match.Groups[3].Value);
    }

    private static string ColumnReference(string table, string column) =>
        Expected.TryGetValue(table, out var columns) && columns.ContainsKey(column) ? $"{table}[{column}]" : throw Failure();

    private static string Name(JsonElement value) => Field(value, "Name").GetString() ?? throw Failure();

    private static bool Try(JsonElement value, string name, out JsonElement found)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Failure();
        var matches = value.EnumerateObject().Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1) throw Failure();
        found = matches.Length == 1 ? matches[0].Value : default;
        return matches.Length == 1;
    }

    private static JsonElement Field(JsonElement value, string name) => Try(value, name, out var found) ? found : throw Failure();

    private static void AssertUnique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var fields = value.EnumerateObject().ToArray();
            if (fields.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != fields.Length) throw Failure();
            foreach (var field in fields) AssertUnique(field.Value);
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) AssertUnique(child);
    }

    private static void CheckCompleteness(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Failure();
        foreach (var field in value.EnumerateObject())
        {
            if (field.Name.Equals("truncated", StringComparison.OrdinalIgnoreCase) ||
                field.Name.Equals("isTruncated", StringComparison.OrdinalIgnoreCase) ||
                field.Name.Equals("hasMore", StringComparison.OrdinalIgnoreCase) ||
                field.Name.Equals("isPartial", StringComparison.OrdinalIgnoreCase))
            {
                if (field.Value.ValueKind != JsonValueKind.False) throw Failure();
            }
            if (new[] { "warning", "warnings", "nextCursor", "continuationToken", "summary", "overview" }
                .Contains(field.Name, StringComparer.OrdinalIgnoreCase) &&
                field.Value.ValueKind != JsonValueKind.Null &&
                !(field.Value.ValueKind == JsonValueKind.Array && field.Value.GetArrayLength() == 0) &&
                !(field.Value.ValueKind == JsonValueKind.String && field.Value.GetString() == ""))
                throw Failure();
        }
    }

    private static DemoException Failure() => new("iq_schema_unrecognized",
        "Live IQ metadata did not match the complete reviewed synthetic schema. Truncated, summary-only and unknown shapes cannot generate DAX; there is no static fallback.", 502);
}
