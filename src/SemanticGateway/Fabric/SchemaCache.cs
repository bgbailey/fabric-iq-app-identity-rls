using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace SemanticGateway.Fabric;

public sealed record ModelSchema(JsonNode Schema, DateTimeOffset RetrievedAtUtc, string Source);

/// <summary>
/// Caches the Fabric IQ schema. The schema is the same for every user (RLS filters rows, not
/// metadata), so one fetch serves everyone. Only the very first read waits for Fabric IQ; after
/// that, an expired copy is still served while a fresh one is read in the background. The last
/// good copy is written to disk and used if Fabric IQ is unreachable.
/// </summary>
public sealed class SchemaCache(FabricIqSchemaClient fabricIq, IOptions<FabricIqSettings> options, ILogger<SchemaCache> log)
{
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private ModelSchema? current;
    private DateTimeOffset refreshAfter;

    private static string SnapshotPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "isv-semantic-gateway", "schema-snapshot.json");

    public async Task<ModelSchema> GetAsync(CancellationToken cancellationToken)
    {
        if (current is null) await RefreshAsync(wait: true, cancellationToken);
        else if (DateTimeOffset.UtcNow >= refreshAfter) _ = Task.Run(() => RefreshAsync(wait: false, CancellationToken.None));
        return current!;
    }

    private async Task RefreshAsync(bool wait, CancellationToken cancellationToken)
    {
        // A background refresh skips if another refresh is already running.
        if (!await refreshLock.WaitAsync(wait ? Timeout.Infinite : 0, cancellationToken)) return;
        try
        {
            if (current is not null && DateTimeOffset.UtcNow < refreshAfter) return;
            try
            {
                var schema = RemoveExcludedTables(JsonNode.Parse(await fabricIq.GetSchemaJsonAsync(cancellationToken))!);
                current = new ModelSchema(schema, DateTimeOffset.UtcNow, "fabric-iq");
                Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath)!);
                await File.WriteAllTextAsync(SnapshotPath, JsonSerializer.Serialize(new { current.RetrievedAtUtc, Schema = schema.ToJsonString() }), cancellationToken);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested && (current ?? LoadSnapshot()) is { } fallback)
            {
                log.LogWarning("Fabric IQ schema refresh failed ({Message}); using the copy from {Time}.", error.Message, fallback.RetrievedAtUtc);
                current = fallback;
            }
            refreshAfter = DateTimeOffset.UtcNow.AddMinutes(options.Value.SchemaCacheMinutes);
        }
        finally
        {
            refreshLock.Release();
        }
    }

    /// <summary>
    /// Fabric IQ returns { "schema": { "Tables": [...], "ActiveRelationships": [...] }, ... }.
    /// Keep the schema and drop tables the model should not see, such as the RLS mapping table.
    /// </summary>
    private JsonNode RemoveExcludedTables(JsonNode document)
    {
        var schema = (document["schema"] ?? document).DeepClone();
        var excluded = options.Value.ExcludeTables;
        bool Mentions(JsonNode? node) => node is not null && excluded.Any(table => node.ToJsonString().Contains(table, StringComparison.OrdinalIgnoreCase));

        if (schema["Tables"] is JsonArray tables)
            foreach (var table in tables.Where(t => excluded.Contains((string?)t?["Name"], StringComparer.OrdinalIgnoreCase)).ToList())
                tables.Remove(table);
        foreach (var key in new[] { "ActiveRelationships", "Relationships" })
            if (schema[key] is JsonArray relationships)
                foreach (var relationship in relationships.Where(Mentions).ToList())
                    relationships.Remove(relationship);
        return schema;
    }

    private static ModelSchema? LoadSnapshot()
    {
        if (!File.Exists(SnapshotPath)) return null;
        var saved = JsonNode.Parse(File.ReadAllText(SnapshotPath))!;
        return new ModelSchema(JsonNode.Parse((string)saved["Schema"]!)!, saved["RetrievedAtUtc"]!.GetValue<DateTimeOffset>(), "snapshot");
    }
}
