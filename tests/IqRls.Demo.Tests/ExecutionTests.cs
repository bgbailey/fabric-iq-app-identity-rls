using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using IqRls.Demo;

namespace IqRls.Demo.Tests;

public sealed class ExecutionTests
{
    private static HttpRequestMessage StreamRequest()
    {
        var request = TestSupport.Ask();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-ndjson"));
        return request;
    }

    private static JsonElement[] Frames(string body) => body.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToArray();

    [Fact]
    public async Task StreamCorrelatesRealStageEventsAndReturnsTheGeneratedQuery()
    {
        using var factory = new DemoFactory();
        const string generated = "EVALUATE ROW(\"Fresh generation\", [Total Amount])";
        factory.Planner.OnPlan = _ => Task.FromResult(
            new QueryPlan(generated, new string('a', 64), "Test schema", "generator-v1", 20, 9, DemoCatalog.MetadataMode));
        using var client = factory.Client();
        using var request = StreamRequest();
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType!.MediaType);
        var frames = Frames(await response.Content.ReadAsStringAsync());
        var terminal = frames[^1];
        Assert.Equal("result", terminal.GetProperty("type").GetString());
        var result = terminal.GetProperty("result");
        Assert.Equal(generated, result.GetProperty("trace").GetProperty("query").GetString());
        Assert.Equal("generator-v1", result.GetProperty("trace").GetProperty("generationModel").GetString());
        Assert.Equal(generated, Assert.Single(factory.Query.Requests).Input.Query);
        var events = frames[..^1].Select(frame => frame.GetProperty("event")).ToArray();
        Assert.Equal(Enumerable.Range(1, events.Length), events.Select(e => e.GetProperty("sequence").GetInt32()));
        Assert.All(events, e => Assert.Equal(result.GetProperty("requestId").GetString(), e.GetProperty("requestId").GetString()));
        var elapsed = events.Select(e => e.GetProperty("elapsedMs").GetInt64()).ToArray();
        Assert.Equal(elapsed.Order(), elapsed);
        Assert.Equal("identity", events[0].GetProperty("step").GetString());
        Assert.Equal("complete", events[^1].GetProperty("step").GetString());
        var queryStart = events.Single(e => e.GetProperty("step").GetString() == "rls-query" &&
            e.GetProperty("status").GetString() == "started");
        using var wire = JsonDocument.Parse(queryStart.GetProperty("details").GetProperty("request").GetString()!);
        Assert.Equal("app-user-A1", wire.RootElement.GetProperty("customData").GetString());
        Assert.Equal("ExternalAppScope", wire.RootElement.GetProperty("roles")[0].GetString());
    }

    [Fact]
    public async Task IqFailureStreamsFailureWithoutPretendingLaterStepsRan()
    {
        using var factory = new DemoFactory();
        factory.Planner.OnPlan = _ => throw new DemoException("iq_auth_required", "Explicit delegated sign-in is required.");
        using var client = factory.Client();
        using var request = StreamRequest();
        using var response = await client.SendAsync(request);
        var frames = Frames(await response.Content.ReadAsStringAsync());
        Assert.Equal("error", frames[^1].GetProperty("type").GetString());
        Assert.Equal("iq_auth_required", frames[^1].GetProperty("error").GetString());
        var events = frames[..^1].Select(f => f.GetProperty("event")).ToArray();
        Assert.Equal("failed", events[^1].GetProperty("status").GetString());
        Assert.Equal("iq-schema", events[^1].GetProperty("step").GetString());
        Assert.DoesNotContain(events, e => new[] { "dax-generation", "rls-query", "explanation", "complete" }
            .Contains(e.GetProperty("step").GetString()));
        Assert.Empty(factory.Query.Requests);
        Assert.Empty(factory.Explainer.Inputs);
    }

    [Fact]
    public async Task StageEventsArriveBeforeWorkCompletes()
    {
        using var factory = new DemoFactory();
        var release = new TaskCompletionSource<QueryPlan>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Planner.OnPlan = token => release.Task.WaitAsync(token);
        using var client = factory.Client();
        using var request = StreamRequest();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var firstLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("\"step\":\"identity\"", firstLine);
        Assert.Empty(factory.Query.Requests);
        release.SetResult(new(FakePlanner.Query, new string('b', 64), "Test schema", "test-model", 1, 1, DemoCatalog.MetadataMode));
        var rest = await reader.ReadToEndAsync();
        Assert.Contains("\"type\":\"result\"", rest);
    }

    [Fact]
    public async Task UnexpectedFaultProducesSafeTerminalErrorAfterHttp200()
    {
        using var factory = new DemoFactory();
        factory.Planner.OnPlan = _ => throw new InvalidOperationException("SECRET_TOKEN_RAW_SCHEMA");
        using var client = factory.Client();
        using var request = StreamRequest();
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SECRET", body);
        Assert.DoesNotContain("\"type\":\"result\"", body);
        Assert.Equal("demo_failed", Frames(body)[^1].GetProperty("error").GetString());
    }

    [Fact]
    public async Task ConsoleLogNeverContainsDetailedPayloadsOrIdentity()
    {
        using var log = new StringWriter();
        var seen = new List<ExecutionEvent>();
        var observer = new ExecutionSession("correlation-123", (item, _) =>
        {
            seen.Add(item);
            return ValueTask.CompletedTask;
        }, log);
        await observer.EmitAsync("dax-generation", "started", "Generate", "delegated-principal",
            new Dictionary<string, string> { ["query"] = "private detailed payload" }, CancellationToken.None);
        await observer.FailAsync("generation_failed", CancellationToken.None);
        var logged = log.ToString();
        Assert.Contains("correlation-123", logged);
        Assert.DoesNotContain("private", logged);
        Assert.DoesNotContain("delegated-principal", logged);
        Assert.Equal("failed", seen[^1].Status);
        Assert.Equal("dax-generation", seen[^1].Step);
    }

    [Fact]
    public async Task PlannerCannotSilentlyReturnStaticSchemaMode()
    {
        using var factory = new DemoFactory();
        factory.Planner.OnPlan = _ => Task.FromResult(
            new QueryPlan(FakePlanner.Query, "", "offline", "model", 1, 1, "offline"));
        using var client = factory.Client();
        using var request = StreamRequest();
        using var response = await client.SendAsync(request);
        Assert.Equal("invalid_plan", Frames(await response.Content.ReadAsStringAsync())[^1].GetProperty("error").GetString());
        Assert.Empty(factory.Query.Requests);
    }
}
