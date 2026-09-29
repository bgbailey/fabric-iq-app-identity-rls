using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using IqRls.Core;
using IqRls.Demo;

namespace IqRls.Demo.Tests;

public sealed class LivePlannerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T00:00:00Z");
    private static readonly Guid TestTenant = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private const string TestPrincipal = "synthetic@example.invalid";
    private static readonly IqConfiguration Iq = new(TestTenant,
        Guid.Parse("22222222-2222-4222-8222-222222222222"), TestPrincipal);
    private const string Query = """EVALUATE ROW("Model Generated Total", COALESCE([Total Amount], 0), "Activity Count", COALESCE([Activity Count], 0))""";
    private const string Schema = """
        {"schema":{
          "Tables":[
            {"Name":"Activity","Columns":[
              {"Name":"Activity Key","Type":"Int64"},{"Name":"Scope Key","Type":"Int64"},
              {"Name":"Date Key","Type":"Int64"},{"Name":"Amount","Type":"Decimal"}],
             "Measures":[{"Name":"Total Amount","Expression":"private-expression"},{"Name":"Activity Count","Expression":"private-expression"}]},
            {"Name":"Scope","Columns":[
              {"Name":"Scope Key","Type":"Int64"},{"Name":"Customer","Type":"String","Description":"private-description","SampleValues":["private-value"]},
              {"Name":"Product","Type":"String"}]},
            {"Name":"Date","Columns":[{"Name":"Date Key","Type":"Int64"},{"Name":"Date","Type":"DateTime"},
              {"Name":"Year","Type":"Int64"},{"Name":"Month","Type":"Int64"},{"Name":"Day","Type":"Int64"}]},
            {"Name":"User Access","Columns":[{"Name":"private-grants","Type":"String"}],"Rows":["private-entitlements"]}],
          "ActiveRelationships":[
            {"PK":"'Scope'[Scope Key]","FK":"'Activity'[Scope Key]"},
            {"PK":"'Date'[Date Key]","FK":"'Activity'[Date Key]"}],
          "CustomInstructions":"private-instructions", "VerifiedAnswers":[{"Query":"private-answer"}]
        }}
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlannerUsesLiveLifecycleSchemaAndFreshGeneratedQuery(bool sse)
    {
        var budget = new CountingBudget();
        var observer = new Observer();
        var methods = new List<string>();
        var modelRequests = 0;
        using var handler = new Handler(async (request, ct) =>
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = document.RootElement;
            if (request.RequestUri!.Host == "synthetic-test.openai.azure.com")
            {
                modelRequests++;
                Assert.Equal("model-test-token", request.Headers.Authorization!.Parameter);
                Assert.False(root.GetProperty("store").GetBoolean());
                Assert.False(root.TryGetProperty("previous_response_id", out _));
                Assert.False(root.TryGetProperty("tools", out _));
                Assert.True(root.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());
                var input = root.GetProperty("input").GetString()!;
                Assert.Contains("Total Amount", input);
                Assert.DoesNotContain("EVALUATE", input);
                Assert.DoesNotContain("private-", input);
                Assert.DoesNotContain("app-user", input);
                Assert.DoesNotContain(TestPrincipal, input);
                Assert.DoesNotContain("User Access", input);
                using var generationInput = JsonDocument.Parse(input);
                Assert.Empty(generationInput.RootElement.GetProperty("reviewedSyntheticHints").EnumerateObject());
                return Json(Generation());
            }
            Assert.Equal(FabricIqSchemaClient.Endpoint, request.RequestUri.AbsoluteUri);
            Assert.Equal(Jwt(), request.Headers.Authorization!.Parameter);
            Assert.Equal(FabricIqSchemaClient.Variant, request.Headers.GetValues("X-Variants").Single());
            Assert.Contains(request.Headers.Accept, a => a.MediaType == "application/json");
            Assert.Contains(request.Headers.Accept, a => a.MediaType == "text/event-stream");
            var method = root.GetProperty("method").GetString()!;
            methods.Add(method);
            if (method != "initialize")
            {
                Assert.Equal("test-session", request.Headers.GetValues("Mcp-Session-Id").Single());
                Assert.Equal("2025-06-18", request.Headers.GetValues("MCP-Protocol-Version").Single());
            }
            if (method == "notifications/initialized")
            {
                Assert.False(root.TryGetProperty("id", out _));
                return new(HttpStatusCode.Accepted) { Content = new ByteArrayContent([]) };
            }
            var id = root.GetProperty("id").GetInt32();
            if (method == "tools/call")
            {
                Assert.Equal(FabricIqSchemaClient.SchemaTool, root.GetProperty("params").GetProperty("name").GetString());
                var args = root.GetProperty("params").GetProperty("arguments");
                Assert.Single(args.EnumerateObject());
                Assert.Equal(Startup().QueryConfiguration!.DatasetId.ToString("D"), args.GetProperty("artifactId").GetString());
            }
            var response = Reply(id, method switch
            {
                "initialize" => """{"protocolVersion":"2025-06-18","capabilities":{"tools":{}},"serverInfo":{"name":"private-name"}}""",
                "tools/list" => Tools(),
                "tools/call" => ToolResult(),
                _ => throw new InvalidOperationException()
            }, sse);
            if (method == "initialize") response.Headers.Add("Mcp-Session-Id", "test-session");
            return response;
        });
        using var http = new HttpClient(handler);
        using var planner = new LiveDemoPlanner(Startup(), budget, new Clock(), http, () => new FixedTokens(Jwt()), new FixedTokens("model-test-token"));
        var question = DemoCatalog.ResolveQuestion("overview");
        var plan = await planner.PlanAsync(question, observer, CancellationToken.None);
        Assert.Equal(Query, plan.Query);
        Assert.Equal(LiveDemoPlanner.MetadataMode, plan.MetadataMode);
        Assert.Equal(5, budget.Calls);
        Assert.Equal(1, modelRequests);
        Assert.Equal(new[] { "initialize", "notifications/initialized", "tools/list", "tools/call" }, methods);
        Assert.Equal(12, observer.Events.Count);
        Assert.Equal(plan.SchemaSummary, observer.Events.Single(e => e.Step == "iq-schema" && e.Status == "completed").Details!["schema"]);
        Assert.Equal(Query, observer.Events.Single(e => e.Step == "dax-generation" && e.Status == "completed").Details!["generatedDax"]);
        Assert.Equal(15, plan.InputTokens);
        Assert.Equal(8, plan.OutputTokens);
        var trace = JsonSerializer.Serialize(observer.Events);
        Assert.DoesNotContain("private-", trace);
        Assert.DoesNotContain("test-session", trace);
        Assert.DoesNotContain(Jwt(), trace);
        Assert.DoesNotContain(TestPrincipal, trace);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("principal")]
    [InlineData("client")]
    [InlineData("audience")]
    [InlineData("scopes")]
    [InlineData("app-only")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("missing-principal")]
    [InlineData("conflicting-principal")]
    public void DelegatedIdentityGuardsRejectMixups(string issue)
    {
        var claims = Claims();
        switch (issue)
        {
            case "tenant": claims["tid"] = "72f988bf-86f1-41af-91ab-2d7cd011db47"; break;
            case "principal": claims["upn"] = "different@example.invalid"; break;
            case "client": claims["appid"] = Guid.NewGuid().ToString(); break;
            case "audience": claims["aud"] = "https://api.fabric.microsoft.com"; break;
            case "scopes": claims["scp"] = "Item.Read.All Dataset.Read.All"; break;
            case "app-only": claims.Remove("scp"); claims["idtyp"] = "app"; break;
            case "expired": claims["exp"] = Now.ToUnixTimeSeconds(); break;
            case "future": claims["nbf"] = Now.AddMinutes(3).ToUnixTimeSeconds(); break;
            case "missing-principal": claims.Remove("upn"); break;
            case "conflicting-principal": claims["preferred_username"] = "other@example.invalid"; break;
        }
        var error = Assert.Throws<DemoException>(() => PinnedIqDelegatedTokens.ValidateIdentity(Jwt(claims), Iq, Now));
        Assert.Equal("iq_identity_rejected", error.Code);
        Assert.DoesNotContain("different@", error.Message);
    }

    [Fact]
    public async Task SilentCredentialNeverFallsBackToInteractiveOrCli()
    {
        var credential = new FakeCredential(_ => throw new AuthenticationRequiredException("private-error",
            new TokenRequestContext(IqConfiguration.Scopes)));
        using var source = new PinnedIqDelegatedTokens(Iq, new Clock(), credential);
        var error = await Assert.ThrowsAsync<DemoException>(() => source.GetTokenAsync(CancellationToken.None).AsTask());
        Assert.Equal("iq_auth_required", error.Code);
        Assert.DoesNotContain("private-error", error.Message);
        Assert.Equal(IqConfiguration.Scopes, credential.RequestedScopes);
        Assert.Equal(1, credential.Calls);
    }

    [Fact]
    public async Task InvalidTokenStopsBeforeAnyMcpRequest()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("must not call")));
        var budget = new CountingBudget();
        var observer = new Observer();
        using var planner = new LiveDemoPlanner(Startup(), budget, new Clock(), http, () => new FixedTokens("not-a-token"), new FixedTokens("model"));
        Assert.Equal("iq_identity_rejected", (await Assert.ThrowsAsync<DemoException>(() =>
            planner.PlanAsync(DemoCatalog.ResolveQuestion("overview"), observer, CancellationToken.None))).Code);
        Assert.Equal(0, budget.Calls);
        Assert.Single(observer.Events);
        Assert.Equal("started", observer.Events[0].Status);
    }

    [Theory]
    [InlineData("missing-table")]
    [InlineData("extra-table")]
    [InlineData("missing-column")]
    [InlineData("unknown-type")]
    [InlineData("missing-measure")]
    [InlineData("missing-relationship")]
    [InlineData("truncated")]
    [InlineData("warning")]
    [InlineData("summary")]
    [InlineData("duplicate-table")]
    [InlineData("case-collision")]
    public void IncompleteOrUnknownSchemaFailsClosed(string issue)
    {
        var node = JsonNode.Parse(Schema)!;
        var schema = node["schema"]!;
        var tables = schema["Tables"]!.AsArray();
        switch (issue)
        {
            case "missing-table": tables.RemoveAt(0); break;
            case "extra-table": tables.Add(new JsonObject { ["Name"] = "Unreviewed" }); break;
            case "missing-column": tables[0]!["Columns"]!.AsArray().RemoveAt(0); break;
            case "unknown-type": tables[0]!["Columns"]![0]!["Type"] = "SomethingElse"; break;
            case "missing-measure": tables[0]!["Measures"]!.AsArray().RemoveAt(0); break;
            case "missing-relationship": schema["ActiveRelationships"]!.AsArray().RemoveAt(0); break;
            case "truncated": node["isTruncated"] = true; break;
            case "warning": schema["warnings"] = new JsonArray("private-truncated"); break;
            case "summary": schema["overview"] = "compact"; break;
            case "duplicate-table": tables[1]!["Name"] = "Activity"; break;
            case "case-collision": schema["tables"] = tables.DeepClone(); break;
        }
        using var result = JsonDocument.Parse(ToolResult(node.ToJsonString()));
        Assert.Equal("iq_schema_unrecognized", Assert.Throws<DemoException>(() =>
            ReviewedSyntheticSchema.FromToolResult(result.RootElement)).Code);
    }

    [Fact]
    public void SchemaProjectionDropsAllPrivilegedAndInstructionBearingFields()
    {
        var schema = Reviewed();
        Assert.DoesNotContain("private-", schema.Json);
        Assert.DoesNotContain("User Access", schema.Json);
        Assert.DoesNotContain("Expression", schema.Json);
        Assert.Contains("Activity[Scope Key]", schema.Json);
        Assert.Equal(64, schema.Hash.Length);
        using var both = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            structuredContent = JsonSerializer.Deserialize<JsonElement>(Schema),
            content = new[] { new { type = "text", text = Schema } }
        }));
        Assert.Equal(schema.Hash, ReviewedSyntheticSchema.FromToolResult(both.RootElement).Hash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiveCitationEnvelopeProjectsOnlyReturnedColumns(bool wrongCitation)
    {
        var id = Guid.Parse("55555555-5555-4555-8555-555555555555");
        var body = JsonNode.Parse(Schema)!;
        var columns = body["schema"]!["Tables"]![0]!["Columns"]!.AsArray();
        columns.RemoveAt(3);
        columns.RemoveAt(0);
        body["semanticModel"] = new JsonObject { ["ArtifactId"] = id.ToString("D") };
        body["schema"]!["ActiveRelationships"]![0]!["UnidirectionalFilter"] = "'Scope' filters 'Activity'";
        body["schema"]!["ActiveRelationships"]![1]!["UnidirectionalFilter"] = "'Date' filters 'Activity'";
        using var result = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = body.ToJsonString() } },
            structuredContent = new { artifact_citation = new
            {
                ArtifactId = wrongCitation ? Guid.NewGuid().ToString("D") : id.ToString("D"),
                Description = "private-description"
            } }
        }));
        if (wrongCitation)
        {
            Assert.Throws<DemoException>(() => ReviewedSyntheticSchema.FromToolResult(result.RootElement, id));
            return;
        }
        var projected = ReviewedSyntheticSchema.FromToolResult(result.RootElement, id);
        Assert.DoesNotContain("Activity Key", projected.Json);
        Assert.DoesNotContain("\"name\":\"Amount\"", projected.Json);
        Assert.DoesNotContain("private-", projected.Json);
        Assert.Contains("Total Amount", projected.Json);
        Assert.Contains("Scope Key", projected.Json);
    }

    [Theory]
    [InlineData("""EVALUATE INFO.TABLES()""")]
    [InlineData("""EVALUATE 'User Access'""")]
    [InlineData("""EVALUATE ROW("Identity", USERNAME())""")]
    [InlineData("""EVALUATE ROW("X", CUSTOMDATA())""")]
    [InlineData("""DEFINE MEASURE 'Activity'[X]=1 EVALUATE ROW("X",[X])""")]
    [InlineData("""EVALUATE ROW("X",[Total Amount]); EVALUATE 'Activity'""")]
    [InlineData("""EVALUATE 'Activity' // comment""")]
    [InlineData("""EVALUATE 'Activity' /* comment */""")]
    [InlineData("""EVALUATE ROW("X",0)""")]
    [InlineData("""EVALUATE 'Activity'[Unknown]""")]
    [InlineData("""EVALUATE ROW("X",[Unknown])""")]
    [InlineData("""EVALUATE ROW("X",[Total Amount]""")]
    [InlineData("""SELECT * FROM Activity""")]
    public void GeneratedDaxRejectsMetadataIdentityAndUnknownConstructs(string query) =>
        Assert.Equal("dax_generation_rejected", Assert.Throws<DemoException>(() => GeneratedDaxPolicy.Validate(query)).Code);

    [Theory]
    [InlineData(Query)]
    [InlineData("""EVALUATE SUMMARIZECOLUMNS('Scope'[Customer],'Scope'[Product],"Total",[Total Amount]) ORDER BY 'Scope'[Customer]""")]
    [InlineData("""EVALUATE CALCULATETABLE(SUMMARIZECOLUMNS('Scope'[Customer],"Total",[Total Amount]), KEEPFILTERS('Scope'[Customer]="B"), KEEPFILTERS('Scope'[Product]="Home"))""")]
    [InlineData("""EVALUATE TOPN(50,SELECTCOLUMNS('Activity',"Activity Key",'Activity'[Activity Key],"Amount",'Activity'[Amount]),[Activity Key],ASC) ORDER BY [Activity Key]""")]
    public void GeneratedDaxAllowsReviewedBusinessQueries(string query) => GeneratedDaxPolicy.Validate(query);

    [Theory]
    [InlineData("""{"query":"EVALUATE 'Activity'","extra":true}""")]
    [InlineData("""{"query":"EVALUATE 'Activity'","query":"EVALUATE 'Scope'"}""")]
    [InlineData("""{"query":null}""")]
    [InlineData("""["EVALUATE 'Activity'"]""")]
    [InlineData("""```json {"query":"EVALUATE 'Activity'"} ```""")]
    public void GenerationRequiresExactlyOneStructuredQuery(string answer) =>
        Assert.Throws<DemoException>(() => ResponsesDaxGenerator.ParseResponse(Encoding.UTF8.GetBytes(Generation(answer))));

    [Theory]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task McpErrorsAreSanitizedAndNotRetried(HttpStatusCode status)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent("private-error-body"),
            Headers = { Location = new Uri("https://elsewhere.invalid") }
        }));
        using var http = new HttpClient(handler);
        var budget = new CountingBudget();
        var client = new FabricIqSchemaClient(http, budget);
        var error = await Assert.ThrowsAsync<DemoException>(() => client.ReadSchemaAsync(Guid.NewGuid(), "test-token", new Observer(), CancellationToken.None));
        Assert.Equal("iq_http_failure", error.Code);
        Assert.DoesNotContain("private", error.Message);
        Assert.Equal(1, budget.Calls);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task BudgetFailurePreventsOutboundRequest()
    {
        using var handler = new Handler((_, _) => throw new InvalidOperationException("must not call"));
        using var http = new HttpClient(handler);
        var budget = new CountingBudget { Maximum = 0 };
        await Assert.ThrowsAsync<DemoException>(() => new FabricIqSchemaClient(http, budget)
            .ReadSchemaAsync(Guid.NewGuid(), "test-token", new Observer(), CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("wrong-id")]
    [InlineData("rpc-error")]
    [InlineData("unknown-version")]
    [InlineData("oversize-json")]
    [InlineData("oversize-sse")]
    [InlineData("malformed-sse")]
    public async Task BoundedMcpTransportRejectsInvalidResponses(string issue)
    {
        using var handler = new Handler((_, _) =>
        {
            var response = issue switch
            {
                "wrong-id" => Reply(7, """{"protocolVersion":"2025-06-18","capabilities":{"tools":{}}}"""),
                "rpc-error" => Json("""{"jsonrpc":"2.0","id":1,"error":{"message":"private-error"}}"""),
                "unknown-version" => Reply(1, """{"protocolVersion":"unknown","capabilities":{"tools":{}}}"""),
                "oversize-json" => Json(new string('x', FabricIqSchemaClient.MaxResponseBytes + 1)),
                "oversize-sse" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', FabricIqSchemaClient.MaxResponseBytes + 1), Encoding.UTF8, "text/event-stream") },
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {broken}\n\n", Encoding.UTF8, "text/event-stream") }
            };
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<DemoException>(() => new FabricIqSchemaClient(http, new CountingBudget())
            .ReadSchemaAsync(Guid.NewGuid(), "test-token", new Observer(), CancellationToken.None));
        Assert.DoesNotContain("private-error", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("""{"type":"object","properties":{"modelId":{"type":"string"}},"required":["modelId"]}""")]
    [InlineData("""{"type":"object","properties":{"artifactId":{"type":"number"}}}""")]
    [InlineData("""{"type":"object","properties":{"artifactId":{"type":"string","pattern":"private-regex"}}}""")]
    [InlineData("""{"type":"object","properties":{"artifactId":{"type":"string"},"other":{"type":"string"}},"required":["artifactId","other"]}""")]
    [InlineData("""{"type":"object","oneOf":[],"properties":{"artifactId":{"type":"string"}}}""")]
    public void RuntimeToolSchemaCannotBeGuessed(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<DemoException>(() => FabricIqSchemaClient.ValidateInputSchema(document.RootElement, Guid.NewGuid()));
    }

    [Fact]
    public void ConfigurationRequiresIqAndMatchingTenant()
    {
        Assert.Equal("iq_auth_required", Assert.Throws<DemoException>(() => DemoConfiguration.Parse("{}")).Code);
        var startup = Startup();
        Assert.Equal(Iq, IqConfiguration.Validate(startup));
        var corporate = startup with { Configuration = startup.Configuration! with { Iq = Iq with { ExpectedPrincipal = "synthetic@microsoft.com" } } };
        Assert.Equal("iq_identity_configuration", Assert.Throws<DemoException>(() => IqConfiguration.Validate(corporate)).Code);
    }

    [Theory]
    [InlineData("login.microsoftonline.com", true)]
    [InlineData("login.windows.net", true)]
    [InlineData("login.microsoft.com", true)]
    [InlineData("sts.windows.net", true)]
    [InlineData("login.microsoftonline.us", false)]
    [InlineData("login.microsoftonline.com.evil.example", false)]
    public void AuthenticationRecordAcceptsOnlyVerifiedPublicCloudAliases(string authority, bool accepted)
    {
        using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new
        {
            username = Iq.ExpectedPrincipal, authority, homeAccountId = $"{Guid.NewGuid():D}.{Iq.TenantId:D}",
            tenantId = Iq.TenantId.ToString("D"), clientId = Iq.ClientId.ToString("D"), version = "1.0"
        }));
        var record = AuthenticationRecord.Deserialize(stream);
        if (accepted) PinnedIqDelegatedTokens.ValidateRecord(record, Iq);
        else Assert.Throws<DemoException>(() => PinnedIqDelegatedTokens.ValidateRecord(record, Iq));
    }

    [Fact]
    public async Task ToolPaginationAndStatelessSessionAreSupportedAndBudgeted()
    {
        var listing = 0;
        using var handler = new Handler(async (request, ct) =>
        {
            Assert.False(request.Headers.Contains("Mcp-Session-Id"));
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var method = root.GetProperty("method").GetString();
            if (method == "notifications/initialized") return new(HttpStatusCode.Accepted) { Content = new ByteArrayContent([]) };
            var id = root.GetProperty("id").GetInt32();
            if (method == "initialize") return Reply(id, """{"protocolVersion":"2025-03-26","capabilities":{"tools":{}}}""");
            Assert.Equal("2025-03-26", request.Headers.GetValues("MCP-Protocol-Version").Single());
            if (method == "tools/list")
            {
                if (++listing == 1) return Reply(id, """{"tools":[],"nextCursor":"private-cursor"}""");
                Assert.Equal("private-cursor", root.GetProperty("params").GetProperty("cursor").GetString());
                return Reply(id, Tools());
            }
            return Reply(id, ToolResult());
        });
        using var http = new HttpClient(handler);
        var budget = new CountingBudget();
        var observer = new Observer();
        var schema = await new FabricIqSchemaClient(http, budget).ReadSchemaAsync(Guid.NewGuid(), "token", observer, CancellationToken.None);
        Assert.Equal(Reviewed().Hash, schema.Hash);
        Assert.Equal(5, budget.Calls);
        Assert.DoesNotContain("private-cursor", JsonSerializer.Serialize(observer.Events));
    }

    [Theory]
    [InlineData("native-only")]
    [InlineData("unsupported-schema")]
    [InlineData("bad-tool-result")]
    [InlineData("bad-notification")]
    public async Task PlannerFailureNeverReachesGenerationOrSubstitutesPreparedQuery(string issue)
    {
        var toolCalls = 0;
        using var handler = new Handler(async (request, ct) =>
        {
            Assert.Equal(new Uri(FabricIqSchemaClient.Endpoint).Host, request.RequestUri!.Host);
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var method = root.GetProperty("method").GetString();
            if (method == "notifications/initialized")
                return new(issue == "bad-notification" ? HttpStatusCode.OK : HttpStatusCode.Accepted) { Content = new ByteArrayContent([]) };
            var id = root.GetProperty("id").GetInt32();
            if (method == "initialize") return Reply(id, """{"protocolVersion":"2025-06-18","capabilities":{"tools":{}}}""");
            if (method == "tools/list")
                return Reply(id, issue switch
                {
                    "native-only" => """{"tools":[{"name":"ExecuteQuery","inputSchema":{}},{"name":"ValueSearch","inputSchema":{}}]}""",
                    "unsupported-schema" => """{"tools":[{"name":"GetSemanticModelSchema","inputSchema":{"type":"object","properties":{"modelId":{"type":"string"}},"required":["modelId"]}}]}""",
                    _ => Tools()
                });
            toolCalls++;
            return Reply(id, """{"isError":true,"content":[{"type":"text","text":"private-error"}]}""");
        });
        using var http = new HttpClient(handler);
        using var planner = new LiveDemoPlanner(Startup(), new CountingBudget(), new Clock(), http, () => new FixedTokens(Jwt()), new FixedTokens("unused"));
        var observer = new Observer();
        var error = await Assert.ThrowsAsync<DemoException>(() => planner.PlanAsync(DemoCatalog.ResolveQuestion("overview"), observer, CancellationToken.None));
        Assert.DoesNotContain("private-error", error.Message);
        Assert.DoesNotContain(observer.Events, e => e.Step == "dax-generation");
        Assert.Equal(issue == "bad-tool-result" ? 1 : 0, toolCalls);
    }

    [Fact]
    public async Task GenerationFailureDoesNotEmitCompletedOrRetry()
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("private-inference-error")
        }));
        using var http = new HttpClient(handler);
        var budget = new CountingBudget();
        var observer = new Observer();
        var generator = new ResponsesDaxGenerator(http, new FixedTokens("model"), Startup().Configuration!.Llm, budget);
        var error = await Assert.ThrowsAsync<DemoException>(() => generator.GenerateAsync(DemoCatalog.ResolveQuestion("overview"), Reviewed(), observer, CancellationToken.None));
        Assert.Equal("dax_generation_http_failure", error.Code);
        Assert.DoesNotContain("private", error.Message);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(1, budget.Calls);
        Assert.Single(observer.Events);
        Assert.Equal("started", observer.Events[0].Status);
    }

    [Fact]
    public async Task CancellationPropagatesWithoutRetry()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler((_, ct) =>
        {
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FabricIqSchemaClient(http, new CountingBudget())
            .ReadSchemaAsync(Guid.NewGuid(), "token", new Observer(), cancellation.Token));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamingBodyLimitWorksWithoutContentLength(bool sse)
    {
        using var handler = new Handler((_, _) =>
        {
            var content = new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes(new string('x', FabricIqSchemaClient.MaxResponseBytes + 1))));
            content.Headers.ContentType = new(sse ? "text/event-stream" : "application/json");
            Assert.Null(content.Headers.ContentLength);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<DemoException>(() => new FabricIqSchemaClient(http, new CountingBudget())
            .ReadSchemaAsync(Guid.NewGuid(), "token", new Observer(), CancellationToken.None));
        Assert.Equal("planner_response_too_large", error.Code);
    }

    private static ReviewedSyntheticSchema Reviewed()
    {
        using var document = JsonDocument.Parse(ToolResult());
        return ReviewedSyntheticSchema.FromToolResult(document.RootElement);
    }
    private static DemoStartup Startup()
    {
        var query = LiveConfiguration.Parse($$"""
            {"authMode":"certificate","tenantId":"{{TestTenant:D}}",
             "clientId":"33333333-3333-4333-8333-333333333333","workspaceId":"44444444-4444-4444-8444-444444444444",
             "datasetId":"55555555-5555-4555-8555-555555555555","certificateThumbprint":"0123456789012345678901234567890123456789",
             "modelAlias":"synthetic-rls-v1","entitlementVersion":"synthetic-v1","role":"ExternalAppScope"}
            """);
        var llm = new LlmConfiguration(new Uri("https://synthetic-test.openai.azure.com"), "synthetic-deployment",
            TestTenant, Guid.Parse("66666666-6666-4666-8666-666666666666"), TestPrincipal);
        return new(@"C:\dev\synthetic-test", new(true, Now.AddHours(1), 100, @"C:\local\query.json", llm, Iq), query, null, null, true);
    }
    private static Dictionary<string, object> Claims() => new()
    {
        ["tid"] = Iq.TenantId.ToString("D"), ["appid"] = Iq.ClientId.ToString("D"),
        ["aud"] = IqConfiguration.Audience, ["upn"] = Iq.ExpectedPrincipal,
        ["scp"] = "Item.Read.All Item.Execute.All Dataset.Read.All",
        ["exp"] = Now.AddHours(1).ToUnixTimeSeconds(), ["nbf"] = Now.AddMinutes(-1).ToUnixTimeSeconds()
    };
    private static string Jwt(Dictionary<string, object>? claims = null)
    {
        var part = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(claims ?? Claims())).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"synthetic.{part}.synthetic";
    }
    private static string ToolResult(string? schema = null) => JsonSerializer.Serialize(new
    {
        isError = false,
        content = new[] { new { type = "text", text = schema ?? Schema } }
    });
    private static string Tools() => """
        {"tools":[
          {"name":"GetSemanticModelSchema","inputSchema":{"type":"object","properties":{"artifactId":{"type":"string","format":"uuid"},"queries":{"type":"array"}},"required":["artifactId"]}},
          {"name":"ExecuteQuery","inputSchema":{}},{"name":"ValueSearch","inputSchema":{}},
          {"name":"UnreviewedFutureTool","inputSchema":{}}]}
        """;
    private static string Generation(string? answer = null) => JsonSerializer.Serialize(new
    {
        status = "completed", model = "synthetic-model-version",
        output = new[] { new { type = "message", role = "assistant", status = "completed", content = new[]
        {
            new { type = "output_text", text = answer ?? JsonSerializer.Serialize(new { query = Query }) }
        } } },
        usage = new { input_tokens = 15, output_tokens = 8 }
    });
    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Reply(int id, string result, bool sse = false)
    {
        var body = $$"""{"jsonrpc":"2.0","id":{{id}},"result":{{result}}}""";
        return sse ? new(HttpStatusCode.OK) { Content = new StringContent(
            ": heartbeat\n\nevent: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{\"private\":\"ignored\"}}\n\n" +
            "event: message\ndata: " + body.Replace("\n", "\ndata: ", StringComparison.Ordinal) + "\n\n", Encoding.UTF8, "text/event-stream") } : Json(body);
    }
    private sealed class Clock : IDemoClock
    {
        public DateTimeOffset UtcNow => Now;
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class CountingBudget : ILiveBudget
    {
        public int Calls { get; private set; }
        public int Maximum { get; init; } = 100;
        public bool LiveEnabled => true;
        public DateTimeOffset? LiveUntilUtc => Now.AddHours(1);
        public void EnsureAvailable()
        {
            if (Calls >= Maximum) throw new DemoException("live_budget_exhausted", "Test budget exhausted.", 429);
        }
        public Task BeforeCloudCallAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureAvailable();
            Calls++;
            return Task.CompletedTask;
        }
    }
    private sealed record Observed(string Step, string Status, IReadOnlyDictionary<string, string>? Details);
    private sealed class Observer : IExecutionObserver
    {
        public List<Observed> Events { get; } = [];
        public ValueTask EmitAsync(string step, string status, string operation, string identity,
            IReadOnlyDictionary<string, string>? details, CancellationToken cancellationToken)
        {
            Events.Add(new(step, status, details));
            return ValueTask.CompletedTask;
        }
    }
    private sealed class FixedTokens(string token) : IAccessTokenSource
    {
        public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken) => ValueTask.FromResult(token);
    }
    private sealed class FakeCredential(Func<TokenRequestContext, AccessToken> get) : TokenCredential
    {
        public int Calls { get; private set; }
        public string[]? RequestedScopes { get; private set; }
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Calls++;
            RequestedScopes = requestContext.Scopes;
            return get(requestContext);
        }
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request, cancellationToken);
        }
    }
    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
