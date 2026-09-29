using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace IqRls.Demo;

public sealed class FabricIqSchemaClient(HttpClient http, ILiveBudget budget, Action<JsonElement>? captureSchema = null)
{
    public const string Endpoint = "https://fabriciq.svc.cloud.microsoft/v1/mcp/fabriciq";
    public const string Variant = "Fabric.Routing.FabricIQ.V1";
    public const string SchemaTool = "GetSemanticModelSchema";
    public const int MaxResponseBytes = 1024 * 1024;
    private const string Identity = "MCAPS DEV delegated metadata user";
    private string? session;
    private string? protocol;
    private int nextId;

    public async Task<ReviewedSyntheticSchema> ReadSchemaAsync(Guid artifactId, string accessToken,
        IExecutionObserver observer, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadSchemaCoreAsync(artifactId, accessToken, observer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw ProtocolFailure();
        }
    }

    private async Task<ReviewedSyntheticSchema> ReadSchemaCoreAsync(Guid artifactId, string accessToken,
        IExecutionObserver observer, CancellationToken cancellationToken)
    {
        if (artifactId == Guid.Empty || nextId != 0) throw ProtocolFailure();
        await Emit(observer, "iq-initialize", "started", "initialize", cancellationToken);
        var initialized = await SendAsync("initialize", new
        {
            protocolVersion = "2025-06-18",
            capabilities = new { },
            clientInfo = new { name = "synthetic-rls-schema-client", version = "1.0" }
        }, accessToken, false, cancellationToken).ConfigureAwait(false);
        protocol = Required(initialized, "protocolVersion").GetString();
        if (protocol is not "2025-06-18" and not "2025-03-26" and not "2025-11-25" ||
            Required(initialized, "capabilities").ValueKind != JsonValueKind.Object ||
            !initialized.GetProperty("capabilities").TryGetProperty("tools", out var toolsCapability) ||
            toolsCapability.ValueKind != JsonValueKind.Object)
            throw ProtocolFailure();
        await Emit(observer, "iq-initialize", "completed", "initialize", cancellationToken,
            new() { ["protocolVersion"] = protocol, ["session"] = session is null ? "not-issued" : "issued-not-disclosed" });
        await Emit(observer, "iq-initialize", "started", "notifications/initialized", cancellationToken);
        await SendAsync("notifications/initialized", null, accessToken, true, cancellationToken).ConfigureAwait(false);
        await Emit(observer, "iq-initialize", "completed", "notifications/initialized", cancellationToken);

        var allTools = new HashSet<string>(StringComparer.Ordinal);
        JsonElement? schemaTool = null;
        string? cursor = null;
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < 4; page++)
        {
            await Emit(observer, "iq-tools", "started", "tools/list", cancellationToken);
            var listed = await SendAsync("tools/list", cursor is null ? new { } : (object)new { cursor },
                accessToken, false, cancellationToken).ConfigureAwait(false);
            var tools = Required(listed, "tools");
            if (tools.ValueKind != JsonValueKind.Array || tools.GetArrayLength() > 64) throw ProtocolFailure();
            foreach (var tool in tools.EnumerateArray())
            {
                var name = Required(tool, "name").GetString();
                if (string.IsNullOrWhiteSpace(name) || name.Length > 128 ||
                    name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-' and not '.') ||
                    !allTools.Add(name) || allTools.Count > 128)
                    throw ProtocolFailure();
                if (name == SchemaTool) schemaTool = tool.Clone();
            }
            var permitted = allTools.Contains(SchemaTool) ? SchemaTool : "";
            var excluded = new[] { "ExecuteQuery", "ValueSearch", "DiscoverArtifacts", "ResolveFabricItem", "GetReportMetadata" }
                .Where(allTools.Contains);
            await Emit(observer, "iq-tools", "completed", "tools/list", cancellationToken, new()
            {
                ["permittedTools"] = permitted,
                ["excludedTools"] = string.Join(", ", excluded),
                ["otherToolsExcluded"] = allTools.Count(t => t != SchemaTool &&
                    !new[] { "ExecuteQuery", "ValueSearch", "DiscoverArtifacts", "ResolveFabricItem", "GetReportMetadata" }.Contains(t)).ToString(),
                ["toolPolicy"] = "Only GetSemanticModelSchema can be dispatched; native ExecuteQuery and ValueSearch are forbidden."
            });
            cursor = listed.TryGetProperty("nextCursor", out var next) && next.ValueKind != JsonValueKind.Null
                ? next.GetString() : null;
            if (cursor is null) break;
            if (cursor.Length is < 1 or > 2048 || !cursors.Add(cursor) || page == 3) throw ProtocolFailure();
        }
        if (schemaTool is null) throw new DemoException("iq_schema_tool_missing", "The live IQ tool list did not expose GetSemanticModelSchema.", 502);
        ValidateInputSchema(Required(schemaTool.Value, "inputSchema"), artifactId);
        await Emit(observer, "iq-schema", "started", "tools/call", cancellationToken,
            new() { ["tool"] = SchemaTool, ["arguments"] = "{\"artifactId\":\"<configured-synthetic-model>\"}" });
        var raw = await SendAsync("tools/call", new { name = SchemaTool, arguments = new { artifactId = artifactId.ToString("D") } },
            accessToken, false, cancellationToken).ConfigureAwait(false);
        captureSchema?.Invoke(raw);
        var reviewed = ReviewedSyntheticSchema.FromToolResult(raw, artifactId);
        await Emit(observer, "iq-schema", "completed", "tools/call", cancellationToken, new()
        {
            ["tool"] = SchemaTool, ["schemaHash"] = reviewed.Hash, ["schema"] = reviewed.Json,
            ["schemaPolicy"] = "Validated synthetic model projection only; entitlement tables, expressions, descriptions, values, custom instructions and verified answers excluded."
        });
        return reviewed;
    }

    public static void ValidateInputSchema(JsonElement schema, Guid artifactId)
    {
        try
        {
            if (Required(schema, "type").GetString() != "object") throw ProtocolFailure();
            RejectUnsupportedConstraints(schema, objectSchema: true);
            var properties = Required(schema, "properties");
            if (properties.ValueKind != JsonValueKind.Object) throw ProtocolFailure();
            var artifact = Required(properties, "artifactId");
            if (Required(artifact, "type").GetString() != "string") throw ProtocolFailure();
            RejectUnsupportedConstraints(artifact);
            if (artifact.TryGetProperty("format", out var format) && format.GetString() is not "uuid" and not "guid")
                throw ProtocolFailure();
            var value = artifactId.ToString("D");
            if ((artifact.TryGetProperty("const", out var constant) && constant.GetString() != value) ||
                (artifact.TryGetProperty("enum", out var values) &&
                 (values.ValueKind != JsonValueKind.Array || !values.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.String && v.GetString() == value))) ||
                (artifact.TryGetProperty("minLength", out var min) && value.Length < min.GetInt32()) ||
                (artifact.TryGetProperty("maxLength", out var max) && value.Length > max.GetInt32()))
                throw ProtocolFailure();
            if (schema.TryGetProperty("required", out var required) &&
                (required.ValueKind != JsonValueKind.Array ||
                 required.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String || v.GetString() != "artifactId")))
                throw ProtocolFailure();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw ProtocolFailure();
        }
    }

    private static void RejectUnsupportedConstraints(JsonElement schema, bool objectSchema = false)
    {
        // A changed schema is not permission to guess required arguments or ignore validation keywords.
        string[] supported = objectSchema
            ? ["type", "properties", "required", "additionalProperties", "title", "description", "$schema"]
            : ["type", "title", "description", "format", "const", "enum", "minLength", "maxLength", "default", "examples",
                "deprecated", "readOnly", "writeOnly"];
        if (schema.EnumerateObject().Any(p => !supported.Contains(p.Name, StringComparer.Ordinal))) throw ProtocolFailure();
    }

    private async Task<JsonElement> SendAsync(string method, object? parameters, string token, bool notification,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            var id = notification ? (int?)null : ++nextId;
            var message = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["method"] = method };
            if (id is not null) message["id"] = id;
            if (parameters is not null) message["params"] = parameters;
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.Add("X-Variants", Variant);
            if (protocol is not null) request.Headers.Add("MCP-Protocol-Version", protocol);
            if (session is not null) request.Headers.Add("Mcp-Session-Id", session);
            request.Content = JsonContent.Create(message);
            await budget.BeforeCloudCallAsync(deadline.Token).ConfigureAwait(false);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new DemoException("iq_http_failure",
                    $"Fabric IQ returned HTTP {(int)response.StatusCode} during {method}. No retry or alternate credential was used.", 502);
            if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessionHeaders))
            {
                var values = sessionHeaders.ToArray();
                if (values.Length != 1 || values[0].Length is < 1 or > 512 ||
                    values[0].Any(c => c < 0x21 || c > 0x7e) ||
                    (method != "initialize" && values[0] != session))
                    throw ProtocolFailure();
                session = values[0];
            }
            if (notification)
            {
                if (response.StatusCode != HttpStatusCode.Accepted ||
                    (await PlannerHttp.ReadBoundedAsync(response.Content, 1, deadline.Token).ConfigureAwait(false)).Length != 0)
                    throw ProtocolFailure();
                return default;
            }
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType == "application/json")
            {
                var body = await PlannerHttp.ReadBoundedAsync(response.Content, MaxResponseBytes, deadline.Token).ConfigureAwait(false);
                return ParseRpc(body, id!.Value, allowNotification: false) ?? throw ProtocolFailure();
            }
            if (mediaType == "text/event-stream")
            {
                if (response.Content.Headers.ContentLength > MaxResponseBytes) throw PlannerHttp.TooLarge();
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                using var bounded = new BoundedPlannerStream(stream, MaxResponseBytes);
                using var reader = new StreamReader(bounded, new UTF8Encoding(false, true));
                var data = new StringBuilder();
                var eventName = "";
                for (var lines = 0; lines < 4096; lines++)
                {
                    var line = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);
                    if (line is null) throw ProtocolFailure();
                    if (line.Length == 0)
                    {
                        if (data.Length == 0) continue;
                        if (eventName is not "" and not "message") throw ProtocolFailure();
                        var result = ParseRpc(Encoding.UTF8.GetBytes(data.ToString()), id!.Value, allowNotification: true);
                        if (result is not null) return result.Value;
                        data.Clear();
                        eventName = "";
                        continue;
                    }
                    if (line[0] == ':') continue;
                    var colon = line.IndexOf(':');
                    var field = colon < 0 ? line : line[..colon];
                    var value = colon < 0 ? "" : line[(colon + 1)..];
                    if (value.StartsWith(' ')) value = value[1..];
                    if (field == "data")
                    {
                        if (data.Length > 0) data.Append('\n');
                        data.Append(value);
                    }
                    else if (field == "event") eventName = value;
                    else if (field is not "id" and not "retry") throw ProtocolFailure();
                }
                throw ProtocolFailure();
            }
            throw ProtocolFailure();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DemoException("iq_timeout", "The Fabric IQ MCP operation exceeded its time limit. No automatic retry was made.", 504);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException or DecoderFallbackException)
        {
            throw ProtocolFailure();
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            throw new DemoException("iq_transport_failed", "The Fabric IQ MCP transport failed. No automatic retry was made.", 502);
        }
    }

    private static JsonElement? ParseRpc(byte[] body, int id, bool allowNotification)
    {
        using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 40 });
        var root = document.RootElement;
        if (Required(root, "jsonrpc").GetString() != "2.0") throw ProtocolFailure();
        if (!root.TryGetProperty("id", out var received))
        {
            if (allowNotification && Required(root, "method").GetString() is "notifications/progress" or "notifications/message")
                return null;
            throw ProtocolFailure();
        }
        if (received.ValueKind != JsonValueKind.Number || received.GetInt32() != id || root.TryGetProperty("method", out _))
            throw ProtocolFailure();
        if (root.TryGetProperty("error", out _))
            throw new DemoException("iq_rpc_failure", "Fabric IQ reported an MCP error. Raw service details are not exposed.", 502);
        return Required(root, "result").Clone();
    }

    internal static JsonElement Required(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) throw ProtocolFailure();
        var properties = element.EnumerateObject().ToArray();
        if (properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length ||
            !element.TryGetProperty(name, out var value)) throw ProtocolFailure();
        return value;
    }

    private static ValueTask Emit(IExecutionObserver observer, string step, string status, string method,
        CancellationToken ct, Dictionary<string, string>? additional = null)
    {
        var details = additional ?? [];
        details["method"] = "POST " + method;
        details["endpoint"] = Endpoint;
        details["variant"] = Variant;
        return observer.EmitAsync(step, status, method, Identity, details, ct);
    }

    internal static DemoException ProtocolFailure() => new("iq_contract_unrecognized",
        "The live IQ protocol or schema contract is incomplete or unrecognized. No generated query or static fallback was used.", 502);
}

internal static class PlannerHttp
{
    internal static DemoException TooLarge() => new("planner_response_too_large", "The planning service response exceeded the bounded local limit.", 502);

    internal static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken ct)
    {
        if (content.Headers.ContentLength > maximum) throw TooLarge();
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var bounded = new BoundedPlannerStream(stream, maximum);
        using var bytes = new MemoryStream();
        await bounded.CopyToAsync(bytes, ct).ConfigureAwait(false);
        return bytes.ToArray();
    }
}

internal sealed class BoundedPlannerStream(Stream inner, int maximum) : Stream
{
    private int total;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => total; set => throw new NotSupportedException(); }
    private int Account(int count)
    {
        total += count;
        if (total > maximum) throw PlannerHttp.TooLarge();
        return count;
    }
    public override int Read(byte[] buffer, int offset, int count) =>
        Account(inner.Read(buffer, offset, Math.Min(count, maximum - total + 1)));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Account(await inner.ReadAsync(buffer[..Math.Min(buffer.Length, maximum - total + 1)], cancellationToken).ConfigureAwait(false));
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
