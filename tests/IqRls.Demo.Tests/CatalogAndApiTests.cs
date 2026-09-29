using System.Net;
using System.Text.Json;
using IqRls.Core;
using IqRls.Demo;

namespace IqRls.Demo.Tests;

public sealed class CatalogAndApiTests
{
    [Fact]
    public async Task CatalogMatchesFrozenContractWithoutQueriesOrFixtureValues()
    {
        using var factory = new DemoFactory { UseRealDisabledBudget = true };
        using var client = factory.Client();
        using var response = await client.GetAsync("/api/demo");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var actual = JsonDocument.Parse(json);
        using var contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "demo-api.json")));
        var expected = contract.RootElement.GetProperty("catalog").GetProperty("response");
        var root = actual.RootElement;
        Assert.Equal(expected.GetProperty("mode").GetString(), root.GetProperty("mode").GetString());
        Assert.Equal("fabric-iq-generated-dax", root.GetProperty("executionMode").GetString());
        Assert.False(root.GetProperty("liveEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("liveUntilUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("llmModel").ValueKind);
        foreach (var key in new[] { "identityNotice", "evidenceNote" })
            Assert.Equal(expected.GetProperty(key).GetString(), root.GetProperty(key).GetString());
        foreach (var key in new[] { "identities", "questions" })
            Assert.Equal(JsonSerializer.Serialize(expected.GetProperty(key)), JsonSerializer.Serialize(root.GetProperty(key)));
        Assert.DoesNotContain("queryHash", json);
        Assert.DoesNotContain("EVALUATE", json);
        Assert.DoesNotContain("1950", json);
        Assert.Equal(SyntheticSubjects.All.Select(s => s.SubjectKey), DemoCatalog.Identities.Select(i => i.Id));
        Assert.Empty(factory.Query.Requests);
        Assert.Empty(factory.Explainer.Inputs);
        Assert.Empty(factory.Planner.Questions);
    }

    [Fact]
    public async Task HealthNeverContactsCloudAndDisabledAskHasNoFallback()
    {
        using var factory = new DemoFactory { UseRealDisabledBudget = true };
        using var client = factory.Client();
        using var health = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Contains("\"cloudChecked\":false", await health.Content.ReadAsStringAsync());
        using var request = TestSupport.Ask();
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("\"error\":\"live_not_enabled\"", await response.Content.ReadAsStringAsync());
        Assert.Empty(factory.Query.Requests);
        Assert.Empty(factory.Explainer.Inputs);
        Assert.Empty(factory.Planner.Questions);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("""{"userId":"app-user-A1","questionId":"overview","query":"EVALUATE ALL('Activity')"}""")]
    [InlineData("""{"userId":"app-user-A1","questionId":"overview","roles":["Admin"]}""")]
    [InlineData("""{"userId":"app-user-A1","questionId":"overview","customData":"app-user-B1"}""")]
    [InlineData("""{"userId":"app-user-A1","userId":"app-user-B1","questionId":"overview"}""")]
    [InlineData("""{"userId":7,"questionId":"overview"}""")]
    [InlineData("""{"userId":null,"questionId":"overview"}""")]
    [InlineData("""{"userId":"APP-USER-A1","questionId":"overview"}""")]
    [InlineData("""{"userId":"app-user-A1 ","questionId":"overview"}""")]
    [InlineData("""{"userId":"app-user-A1","questionId":"freeform"}""")]
    [InlineData("""{"userId":"app-user-unknown","questionId":"overview"}""")]
    public async Task InvalidRequestsNeverReachEitherRuntime(string json)
    {
        using var factory = new DemoFactory();
        using var client = factory.Client();
        using var request = TestSupport.Ask(json);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(3, error.RootElement.EnumerateObject().Count());
        Assert.NotEmpty(error.RootElement.GetProperty("requestId").GetString()!);
        Assert.Empty(factory.Query.Requests);
        Assert.Empty(factory.Explainer.Inputs);
        Assert.Equal(0, factory.Budget.Calls);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("null")]
    [InlineData("http://localhost:5187")]
    [InlineData("http://127.0.0.1:9999")]
    public async Task RejectsCrossOrigin(string origin)
    {
        using var factory = new DemoFactory();
        using var client = factory.Client();
        using var request = TestSupport.Ask(origin: origin);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Empty(factory.Query.Requests);
    }

    [Theory]
    [InlineData("evil.example:5187")]
    [InlineData("localhost:5187")]
    [InlineData("127.0.0.1:80")]
    [InlineData("127.0.0.1")]
    public async Task RejectsUnexpectedHost(string host)
    {
        using var factory = new DemoFactory();
        using var client = factory.Client();
        using var request = TestSupport.Ask();
        request.Headers.Host = host;
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(factory.Query.Requests);
    }

    [Fact]
    public async Task RejectsNonLoopbackEvenWithLoopbackHost()
    {
        using var factory = new DemoFactory { RemoteIp = IPAddress.Parse("192.0.2.5") };
        using var client = factory.Client();
        using var response = await client.GetAsync("/api/demo");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("missing-header")]
    [InlineData("wrong-header")]
    [InlineData("form-content")]
    [InlineData("oversize")]
    [InlineData("fetch-cross-site")]
    public async Task RequestGuardsRunBeforeCloud(string mode)
    {
        using var factory = new DemoFactory();
        using var client = factory.Client();
        using var request = TestSupport.Ask();
        if (mode == "missing-header") request.Headers.Remove("X-Demo-Request");
        if (mode == "wrong-header") request.Headers.Add("X-Demo-Request", "2");
        if (mode == "form-content") request.Content = new StringContent("userId=app-user-A1");
        if (mode == "oversize") request.Content = new StringContent(new string('x', 1100), System.Text.Encoding.UTF8, "application/json");
        if (mode == "fetch-cross-site") request.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var response = await client.SendAsync(request);
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.Forbidden });
        Assert.Empty(factory.Query.Requests);
        Assert.Empty(factory.Explainer.Inputs);
    }

    [Fact]
    public async Task ValidApiReturnsActualRuntimeRowsAnswerModelAndTokens()
    {
        using var factory = new DemoFactory();
        using var client = factory.Client();
        using var request = TestSupport.Ask(origin: DemoStartup.Address);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        Assert.Equal("llm", root.GetProperty("answerSource").GetString());
        Assert.Equal("Test-only explanation of supplied rows.", root.GetProperty("answer").GetString());
        Assert.Equal("250.0000", root.GetProperty("data").GetProperty("rows")[0][0].GetString());
        Assert.Equal("decimal128(19,4)", root.GetProperty("data").GetProperty("columns")[0].GetProperty("arrowType").GetString());
        var trace = root.GetProperty("trace");
        Assert.Equal(HarnessContract.Role, trace.GetProperty("role").GetString());
        Assert.Equal("app-user-A1", trace.GetProperty("customData").GetString());
        Assert.Equal(11, trace.GetProperty("inputTokens").GetInt32());
        Assert.Equal(7, trace.GetProperty("outputTokens").GetInt32());
        Assert.Equal("test-model", trace.GetProperty("model").GetString());
        Assert.Equal(DemoCatalog.MetadataMode, trace.GetProperty("metadataMode").GetString());
        Assert.Equal("test-generator", trace.GetProperty("generationModel").GetString());
        Assert.Equal(13, trace.GetProperty("generationInputTokens").GetInt32());
        Assert.Equal(19, trace.GetProperty("generationOutputTokens").GetInt32());
        Assert.Equal(64, trace.GetProperty("schemaHash").GetString()!.Length);
        Assert.Equal(64, trace.GetProperty("queryHash").GetString()!.Length);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(2, factory.Budget.Calls);
    }

    [Fact]
    public async Task UnexpectedExceptionDoesNotExposeServiceBodyOrCredentials()
    {
        using var factory = new DemoFactory();
        factory.Explainer.OnExplain = _ => throw new InvalidOperationException("SECRET_TOKEN_AND_RAW_BODY");
        using var client = factory.Client();
        using var request = TestSupport.Ask();
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SECRET", json);
        Assert.DoesNotContain("\"answer\"", json);
        Assert.Contains("\"error\":\"demo_failed\"", json);
    }
}
