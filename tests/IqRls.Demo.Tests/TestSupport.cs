using System.Collections.Immutable;
using System.Net;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using IqRls.Core;
using IqRls.Demo;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IqRls.Demo.Tests;

internal static class TestSupport
{
    internal const string QueryConfigurationJson = """
        {
          "authMode":"certificate",
          "tenantId":"11111111-1111-4111-8111-111111111111",
          "clientId":"22222222-2222-4222-8222-222222222222",
          "workspaceId":"33333333-3333-4333-8333-333333333333",
          "datasetId":"44444444-4444-4444-8444-444444444444",
          "certificateThumbprint":"0123456789012345678901234567890123456789",
          "modelAlias":"synthetic-rls-v1",
          "entitlementVersion":"synthetic-v1",
          "role":"ExternalAppScope"
        }
        """;
    internal const string ConfigurationJson = """
        {
          "liveEnabled":true,
          "liveUntilUtc":"2030-01-01T01:00:00Z",
          "maxCalls":100,
          "queryConfigPath":"C:\\local\\synthetic-test\\query.local.json",
          "iq":{
            "tenantId":"11111111-1111-4111-8111-111111111111",
            "clientId":"66666666-6666-4666-8666-666666666666",
            "expectedPrincipal":"synthetic@example.invalid"
          },
          "llm":{
            "endpoint":"https://synthetic-test.openai.azure.com",
            "deployment":"synthetic-gpt-deployment",
            "tenantId":"11111111-1111-4111-8111-111111111111",
            "subscriptionId":"55555555-5555-4555-8555-555555555555",
            "expectedPrincipal":"synthetic@example.invalid"
          }
        }
        """;
    internal static DemoConfiguration Configuration => DemoConfiguration.Parse(ConfigurationJson);
    internal static QueryResult Rowset(string total = "250.0000", string count = "2") =>
        new([new("Total", "decimal128(19,4)", true), new("Activity Count", "int64", true)], [[total, count]]);

    internal static byte[] Arrow(long total, long count)
    {
        using var stream = new MemoryStream();
        var schema = new Schema([new Field("Total", Int64Type.Default, false), new Field("Activity Count", Int64Type.Default, false)], null);
        using (var writer = new ArrowStreamWriter(stream, schema, leaveOpen: true))
        {
            writer.WriteStart();
            using var batch = new RecordBatch(schema,
                [new Int64Array.Builder().Append(total).Build(), new Int64Array.Builder().Append(count).Build()], 1);
            writer.WriteRecordBatch(batch);
            writer.WriteEnd();
        }
        return stream.ToArray();
    }

    internal const string CompletedResponse = """
        {
          "status":"completed",
          "model":"synthetic-actual-model-version",
          "output":[
            {"type":"reasoning","summary":[]},
            {"type":"message","role":"assistant","status":"completed","content":[
              {"type":"output_text","text":"The returned total is 250 across 2 records.","annotations":[]}
            ]}
          ],
          "usage":{"input_tokens":123,"output_tokens":37}
        }
        """;

    internal static HttpRequestMessage Ask(string json = """{"userId":"app-user-A1","questionId":"overview"}""",
        string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/ask");
        request.Headers.Add("X-Demo-Request", "1");
        if (origin is not null) request.Headers.Add("Origin", origin);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }
}

internal sealed class FakeClock : IDemoClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2030-01-01T00:00:00Z");
    public List<TimeSpan> Delays { get; } = [];
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Delays.Add(delay);
        UtcNow += delay;
        return Task.CompletedTask;
    }
}

internal sealed class FakeBudget(FakeClock clock) : ILiveBudget
{
    public bool LiveEnabled { get; set; } = true;
    public DateTimeOffset? LiveUntilUtc { get; set; } = clock.UtcNow.AddMinutes(10);
    public int Calls { get; private set; }
    public int MaxCalls { get; set; } = 100;
    public void EnsureAvailable()
    {
        if (!LiveEnabled) throw new DemoException("live_not_enabled", "Disabled.");
        if (clock.UtcNow >= LiveUntilUtc) throw new DemoException("live_expired", "Expired.");
        if (Calls >= MaxCalls) throw new DemoException("live_budget_exhausted", "Exhausted.", 429);
    }
    public Task BeforeCloudCallAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureAvailable();
        Calls++;
        return Task.CompletedTask;
    }
}

internal sealed class FakeQuery : IDemoQuery
{
    public List<(ServerContext Context, DaxToolInput Input)> Requests { get; } = [];
    public Func<ServerContext, CancellationToken, Task<QueryResult>> OnQuery { get; set; } =
        (_, _) => Task.FromResult(TestSupport.Rowset());
    public Task<QueryResult> ExecuteAsync(ServerContext context, DaxToolInput input, CancellationToken cancellationToken)
    {
        Requests.Add((context, input));
        return OnQuery(context, cancellationToken);
    }
}

internal sealed class FakePlanner : IDemoPlanner
{
    internal const string Query = "EVALUATE ROW(\"Total\", COALESCE([Total Amount], 0), \"Activity Count\", COALESCE([Activity Count], 0))";
    internal readonly List<DemoQuestion> Questions = [];
    internal Func<CancellationToken, Task<QueryPlan>> OnPlan = _ => Task.FromResult(
        new QueryPlan(Query, new string('b', 64), "Test-only schema", "test-generator", 13, 19, DemoCatalog.MetadataMode));

    public async Task<QueryPlan> PlanAsync(DemoQuestion question, IExecutionObserver observer, CancellationToken cancellationToken)
    {
        Questions.Add(question);
        foreach (var step in new[] { "iq-auth", "iq-initialize", "iq-tools" })
        {
            await observer.EmitAsync(step, "started", "Test-only planner fixture", "Test delegated identity", null, cancellationToken);
            await observer.EmitAsync(step, "completed", "Test-only planner fixture", "Test delegated identity", null, cancellationToken);
        }
        await observer.EmitAsync("iq-schema", "started", "Test-only IQ schema adapter", "Test delegated identity", null, cancellationToken);
        var result = await OnPlan(cancellationToken);
        await observer.EmitAsync("iq-schema", "completed", "Test-only IQ schema adapter", "Test delegated identity", null, cancellationToken);
        await observer.EmitAsync("dax-generation", "started", "Test-only generation", "Test model caller", null, cancellationToken);
        await observer.EmitAsync("dax-generation", "completed", "Test-only generation", "Test model caller",
            new Dictionary<string, string> { ["query"] = result.Query }, cancellationToken);
        return result;
    }
}

internal sealed class FakeExplainer : IDemoExplainer
{
    public List<string> Inputs { get; } = [];
    public Func<CancellationToken, Task<LlmAnswer>> OnExplain { get; set; } =
        _ => Task.FromResult(new LlmAnswer("Test-only explanation of supplied rows.", "test-model", 11, 7));
    public Task<LlmAnswer> ExplainAsync(string preparedInput, CancellationToken cancellationToken)
    {
        Inputs.Add(preparedInput);
        return OnExplain(cancellationToken);
    }
}

internal sealed class TestTokens : IAccessTokenSource
{
    internal int Calls;
    public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return ValueTask.FromResult("synthetic-not-a-real-token");
    }
}

internal sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
{
    internal int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return callback(request, cancellationToken);
    }
}

internal sealed class DemoFactory : IDisposable
{
    private WebApplication? app;
    internal readonly FakeClock Clock = new();
    internal readonly FakeQuery Query = new();
    internal readonly FakePlanner Planner = new();
    internal readonly FakeExplainer Explainer = new();
    internal readonly FakeBudget Budget;
    internal bool UseRealDisabledBudget;
    internal IPAddress RemoteIp = IPAddress.Loopback;

    internal DemoFactory() => Budget = new(Clock);

    internal HttpClient Client()
    {
        app ??= DemoApplication.Build([], builder =>
        {
            builder.WebHost.UseTestServer();
            var services = builder.Services;
            services.RemoveAll<IDemoClock>();
            services.AddSingleton<IDemoClock>(Clock);
            if (!UseRealDisabledBudget)
            {
                services.RemoveAll<ILiveBudget>();
                services.AddSingleton<ILiveBudget>(Budget);
            }
            services.RemoveAll<IDemoQuery>();
            services.AddSingleton<IDemoQuery>(Query);
            services.RemoveAll<IDemoPlanner>();
            services.AddSingleton<IDemoPlanner>(Planner);
            services.RemoveAll<IDemoExplainer>();
            services.AddSingleton<IDemoExplainer>(Explainer);
            services.AddSingleton<IStartupFilter>(new RemoteIpFilter(RemoteIp));
        });
        app.StartAsync().GetAwaiter().GetResult();
        var client = app.GetTestClient();
        client.BaseAddress = new Uri(DemoStartup.Address);
        return client;
    }

    public void Dispose() => app?.DisposeAsync().AsTask().GetAwaiter().GetResult();

    private sealed class RemoteIpFilter(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, callNext) =>
            {
                context.Connection.RemoteIpAddress = address;
                return callNext(context);
            });
            next(app);
        };
    }
}
