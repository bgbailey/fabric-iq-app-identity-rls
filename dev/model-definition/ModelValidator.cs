using System.Text;
using System.Text.Json;
using Microsoft.AnalysisServices.Tabular;

namespace ModelDefinition;

internal sealed record ValidatedDefinition(SortedDictionary<string, byte[]> Parts, byte[] FixtureBytes, Fixture Fixture);

internal static class ModelValidator
{
    internal const string ScopeFilter = """
        VAR _subject = CUSTOMDATA()
        VAR _scope = 'Scope'[Scope Key]
        RETURN
            NOT ISBLANK(_subject)
                && _subject <> ""
                && COUNTROWS(
                    FILTER(
                        'User Access',
                        EXACT('User Access'[Subject Key], _subject)
                            && 'User Access'[Scope Key] == _scope
                    )
                ) > 0
        """;
    internal const string AccessFilter = """
        VAR _subject = CUSTOMDATA()
        RETURN
            NOT ISBLANK(_subject)
                && _subject <> ""
                && EXACT('User Access'[Subject Key], _subject)
        """;


    internal const string UsernameScopeFilter = """
        VAR _subject = USERNAME()
        VAR _scope = 'Scope'[Scope Key]
        RETURN
            NOT ISBLANK(_subject)
                && _subject <> ""
                && COUNTROWS(
                    FILTER(
                        'User Access',
                        EXACT('User Access'[Subject Key], _subject)
                            && 'User Access'[Scope Key] == _scope
                    )
                ) > 0
        """;
    internal const string UsernameAccessFilter = """
        VAR _subject = USERNAME()
        RETURN
            NOT ISBLANK(_subject)
                && _subject <> ""
                && EXACT('User Access'[Subject Key], _subject)
        """;

    internal static readonly string[] RequiredParts =
    [
        "definition.pbism",
        "definition/database.tmdl",
        "definition/model.tmdl",
        "definition/relationships.tmdl",
        "definition/roles/ExternalAppScope.tmdl",
        "definition/roles/ExternalAppUsername.tmdl",
        "definition/roles/MetadataOnly.tmdl",
        "definition/tables/Activity.tmdl",
        "definition/tables/Date.tmdl",
        "definition/tables/Scope.tmdl",
        "definition/tables/User Access.tmdl"
    ];

    internal static ValidatedDefinition Load(string root)
    {
        var modelPath = Path.Combine(root, "model", "Synthetic.SemanticModel");
        var parts = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(modelPath, "*", SearchOption.AllDirectories))
            parts.Add(Path.GetRelativePath(modelPath, path).Replace('\\', '/'), File.ReadAllBytes(path));
        var fixtureBytes = File.ReadAllBytes(Path.Combine(root, "model", "fixtures.json"));
        var fixture = Fixture.Read(fixtureBytes);
        ValidateParts(root, parts, fixture);
        return new(parts, fixtureBytes, fixture);
    }

    internal static void ValidateParts(string root, SortedDictionary<string, byte[]> parts, Fixture fixture)
    {
        fixture.Validate();
        Check.Sequence(parts.Keys, RequiredParts, "Complete exact definition part set required; no .platform or extra files.");
        using (var pbism = JsonDocument.Parse(parts["definition.pbism"]))
        {
            Check.That(pbism.RootElement.GetProperty("version").GetString() == "4.2", "PBISM version 4.2 required.");
            Check.That(pbism.RootElement.GetProperty("$schema").GetString() ==
                "https://developer.microsoft.com/json-schemas/fabric/item/semanticModel/definitionProperties/1.0.0/schema.json",
                "Unexpected PBISM schema.");
        }
        var refs = Encoding.UTF8.GetString(parts["definition/model.tmdl"]).Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n').Where(line => line.TrimStart().StartsWith("ref ", StringComparison.Ordinal)).Select(line => line.Trim());
        Check.Sequence(refs, new[]
        {
            "ref table Scope", "ref table Activity", "ref table Date", "ref table 'User Access'",
            "ref role ExternalAppScope", "ref role ExternalAppUsername", "ref role MetadataOnly"
        }, "Required table/role references");

        WithWorkDirectory(root, work =>
        {
            foreach (var (relative, bytes) in parts)
            {
                var destination = Path.Combine(work, "input", relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, bytes);
            }
            using var database = TmdlSerializer.DeserializeDatabaseFromFolder(Path.Combine(work, "input", "definition"));
            ValidateMetadata(database, fixture);
            var serializedPath = Path.Combine(work, "roundtrip");
            TmdlSerializer.SerializeDatabaseToFolder(database, serializedPath);
            using var roundtrip = TmdlSerializer.DeserializeDatabaseFromFolder(serializedPath);
            ValidateMetadata(roundtrip, fixture);
            Check.That(TmdlSerializer.SerializeDatabase(database) == TmdlSerializer.SerializeDatabase(roundtrip),
                "TOM round-trip changed metadata, expressions, or roles.");
        });
    }

    private static void ValidateMetadata(Database database, Fixture fixture)
    {
        Check.That(database.CompatibilityLevel == 1702 && database.CompatibilityMode.ToString() == "PowerBI",
            "Compatibility must be PowerBI 1702.");
        var model = database.Model;
        Check.That(model.DefaultMode == ModeType.Import &&
            model.DefaultPowerBIDataSourceVersion.ToString().Equals("PowerBI_V3", StringComparison.OrdinalIgnoreCase),
            "Import and powerBI_V3 required.");
        Check.That(model.DataSources.Count == 0 && model.Expressions.Count == 0,
            "No data sources or shared external expressions allowed.");
        Check.Sequence(model.Tables.Select(table => table.Name).Order(StringComparer.Ordinal),
            new[] { "Activity", "Date", "Scope", "User Access" }, "Exact table set");
        var columns = new Dictionary<string, (string Name, DataType Type)[]>(StringComparer.Ordinal)
        {
            ["Scope"] = [("Scope Key", DataType.Int64), ("Customer", DataType.String), ("Product", DataType.String)],
            ["Activity"] = [("Activity Key", DataType.Int64), ("Scope Key", DataType.Int64), ("Date Key", DataType.Int64), ("Amount", DataType.Decimal)],
            ["User Access"] = [("Subject Key", DataType.String), ("Scope Key", DataType.Int64)],
            ["Date"] = [("Date Key", DataType.Int64), ("Date", DataType.DateTime), ("Year", DataType.Int64), ("Month", DataType.Int64), ("Day", DataType.Int64)]
        };
        var expressions = fixture.InlineExpressions();
        foreach (var table in model.Tables)
        {
            Check.That(!string.IsNullOrWhiteSpace(table.Description), $"{table.Name}: description required.");
            Check.Sequence(table.Columns.Select(column => (column.Name, column.DataType)), columns[table.Name],
                $"{table.Name}: exact typed columns");
            foreach (var column in table.Columns)
            {
                Check.That(column is DataColumn data && data.SourceColumn == column.Name &&
                    column.SummarizeBy == AggregateFunction.None, $"{table.Name}.{column.Name}: source/type mapping");
                Check.That(!string.IsNullOrWhiteSpace(column.Description), $"{table.Name}.{column.Name}: description");
            }
            Check.That(table.Partitions.Count == 1 && table.Partitions[0].Mode == ModeType.Import
                && table.Partitions[0].Source is MPartitionSource, $"{table.Name}: one Import M partition");
            var source = (MPartitionSource)table.Partitions[0].Source;
            // Exact comparison with the typed inline fixture disallows external connectors; not an M evaluator.
            Check.That(Check.Expression(source.Expression) == Check.Expression(expressions[table.Name]),
                $"{table.Name}: partition must equal its complete inline fixture expression.");
            Check.That(table.Measures.Count == (table.Name == "Activity" ? 2 : 0), $"{table.Name}: unexpected measure set");
        }
        var activity = model.Tables["Activity"];
        Check.That(Check.Expression(activity.Measures["Total Amount"].Expression) == "SUM('Activity'[Amount])"
            && activity.Measures["Total Amount"].FormatString == "#,##0.00", "Total Amount expression and format");
        Check.That(Check.Expression(activity.Measures["Activity Count"].Expression) == "COUNTROWS('Activity')"
            && activity.Measures["Activity Count"].FormatString == "#,##0", "Activity Count expression and format");
        Check.That(model.Tables["Date"].DataCategory == "Time" && model.Tables["Date"].Columns["Date"].IsKey,
            "Unique neutral calendar date required.");
        Check.That(model.Relationships.Count == 2, "Only Scope and Date relationships permitted.");
        foreach (var relationship in model.Relationships)
        {
            Check.That(relationship is SingleColumnRelationship, "No many-to-many relationship permitted.");
            var single = (SingleColumnRelationship)relationship;
            Check.That(single.IsActive && single.FromCardinality == RelationshipEndCardinality.Many
                && single.ToCardinality == RelationshipEndCardinality.One
                && single.CrossFilteringBehavior == CrossFilteringBehavior.OneDirection
                && single.SecurityFilteringBehavior == SecurityFilteringBehavior.OneDirection,
                $"{single.Name}: active one-to-many, single-direction query and security filtering required.");
            var target = single.Name switch
            {
                "Activity to Scope" => "Scope",
                "Activity to Date" => "Date",
                _ => throw new InvalidDataException("Unexpected relationship.")
            };
            Check.That(single.FromTable.Name == "Activity" && single.ToTable.Name == target
                && single.FromColumn.Name == target + " Key" && single.ToColumn.Name == target + " Key",
                "Wrong relationship endpoints.");
        }
        Check.Sequence(model.Roles.Select(role => role.Name).Order(StringComparer.Ordinal),
            new[] { "ExternalAppScope", "ExternalAppUsername", "MetadataOnly" }, "Exact role set");
        foreach (var role in model.Roles)
        {
            Check.That(role.ModelPermission == ModelPermission.Read && role.Members.Count == 0,
                $"{role.Name}: read only, no memberships");
            Check.Sequence(role.TablePermissions.Select(permission => permission.Table.Name).Order(StringComparer.Ordinal),
                new[] { "Scope", "User Access" }, $"{role.Name}: direct Scope AND entitlement row filters required.");
            foreach (var permission in role.TablePermissions)
            {
                var expected = role.Name switch
                {
                    "MetadataOnly" => "FALSE()",
                    "ExternalAppUsername" => permission.Table.Name == "Scope" ? UsernameScopeFilter : UsernameAccessFilter,
                    _ => permission.Table.Name == "Scope" ? ScopeFilter : AccessFilter
                };
                Check.That(Check.Expression(permission.FilterExpression) == Check.Expression(expected),
                    $"{role.Name}/{permission.Table.Name}: exact fail-closed security policy required.");
            }
        }
    }

    internal static void WithWorkDirectory(string root, Action<string> action)
    {
        var work = Path.Combine(root, "artifacts", "model-definition", "checks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try { action(work); }
        finally { Directory.Delete(work, recursive: true); }
    }
}
