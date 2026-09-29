using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using IqRls.Core;
using IqRls.Demo;

namespace IqRls.Demo.Tests;

public sealed class RunnerTests
{
    [Fact]
    public async Task EverySubjectFlowsThroughRealCoreBrokerWithoutOverridingGeneratedQuery()
    {
        var expected = new Dictionary<string, (long Total, long Count)>
        {
            ["app-user-A1"] = (250, 2), ["app-user-A2"] = (100, 2), ["app-user-A3"] = (350, 4),
            ["app-user-B1"] = (700, 1), ["app-user-pairs"] = (1150, 3), ["app-user-none"] = (0, 0)
        };
        var sent = new List<string>();
        var subjects = new List<string>();
        using var handler = new RecordingHandler(async (request, _) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("api.powerbi.com", request.RequestUri!.Host);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = payload.RootElement;
            Assert.Equal(HarnessContract.Role, root.GetProperty("roles")[0].GetString());
            Assert.Equal(1, root.GetProperty("roles").GetArrayLength());
            var subject = root.GetProperty("customData").GetString()!;
            sent.Add(root.GetProperty("query").GetString()!);
            subjects.Add(subject);
            var row = expected[subject];
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TestSupport.Arrow(row.Total, row.Count)) };
        });
        using var http = new HttpClient(handler);
        var query = new BrokerDemoQuery(new QueryBroker(http, new TestTokens(), LiveConfiguration.Parse(TestSupport.QueryConfigurationJson)));
        var clock = new FakeClock();
        var budget = new FakeBudget(clock);
        var llm = new FakeExplainer();
        var planner = new FakePlanner();
        using var runner = new DemoRunner(planner, query, llm, budget, clock);
        foreach (var subject in DemoCatalog.Identities)
        {
            var result = await runner.AskAsync(new(subject.Id, "overview"), Guid.NewGuid().ToString(), CancellationToken.None);
            Assert.Equal(expected[subject.Id].Total.ToString(), result.Data.Rows[0][0]);
            Assert.Equal(subject.Id, result.Trace.CustomData);
            Assert.Equal(HarnessContract.Role, result.Trace.Role);
            Assert.Equal("llm", result.AnswerSource);
        }
        Assert.Equal(6, handler.Calls);
        Assert.Single(sent.Distinct(StringComparer.Ordinal));
        Assert.Equal(FakePlanner.Query, sent[0]);
        Assert.Equal(6, planner.Questions.Count);
        Assert.Equal(DemoCatalog.Identities.Select(i => i.Id), subjects);
        Assert.Equal(12, budget.Calls);
    }

    [Theory]
    [InlineData("overview")]
    [InlineData("breakdown")]
    [InlineData("daily")]
    [InlineData("details")]
    [InlineData("customer-b-home")]
    public async Task NoPersonaGrantsFixtureOtherResultsOrDaxEnterLlm(string questionId)
    {
        var query = new FakeQuery { OnQuery = (_, _) => Task.FromResult(TestSupport.Rowset("123.4567", "9")) };
        var llm = new FakeExplainer();
        var clock = new FakeClock();
        using var runner = new DemoRunner(new FakePlanner(), query, llm, new FakeBudget(clock), clock);
        _ = await runner.AskAsync(new("app-user-A1", questionId), "request", CancellationToken.None);
        var prompt = Assert.Single(llm.Inputs);
        using var json = JsonDocument.Parse(prompt);
        Assert.Equal(new[] { "question", "data" }, json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(DemoCatalog.ResolveQuestion(questionId).Text, json.RootElement.GetProperty("question").GetString());
        Assert.Equal("123.4567", json.RootElement.GetProperty("data").GetProperty("rows")[0][0].GetString());
        foreach (var forbidden in new[] { "app-user", "ExternalAppScope", "scopeKeys", "1950", "1150", "700", "EVALUATE", "entitlement", "identities" })
            Assert.DoesNotContain(forbidden, prompt);
        Assert.Equal(HarnessContract.ModelAlias, Assert.Single(query.Requests).Context.ModelAlias);
        Assert.Equal(HarnessContract.EntitlementVersion, query.Requests[0].Context.EntitlementVersion);
    }

    [Fact]
    public void PreparedCatalogContainsQuestionsOnlyAndNoExecutableDax()
    {
        Assert.Equal(5, DemoCatalog.Questions.Length);
        foreach (var question in DemoCatalog.Questions)
        {
            Assert.DoesNotContain("CUSTOMDATA", question.Text);
            Assert.DoesNotContain("app-user", question.Text);
            Assert.DoesNotContain("EVALUATE", JsonSerializer.Serialize(question));
        }
        Assert.Null(typeof(DemoQuestion).GetProperty("Query"));
    }

    [Fact]
    public async Task EmptyScopedRowsStillRequireRealLlmNotFallbackAndTransmitNoOtherContext()
    {
        var query = new FakeQuery { OnQuery = (_, _) => Task.FromResult(new QueryResult([new("Total", "int64", true)], [])) };
        var llm = new FakeExplainer { OnExplain = _ => Task.FromResult(new LlmAnswer("No authorized data was returned for this question.", "test-model", 15, 8)) };
        var clock = new FakeClock();
        using var runner = new DemoRunner(new FakePlanner(), query, llm, new FakeBudget(clock), clock);
        var result = await runner.AskAsync(new("app-user-none", "breakdown"), "empty", CancellationToken.None);
        Assert.Equal("llm", result.AnswerSource);
        Assert.Empty(result.Data.Rows);
        Assert.Single(llm.Inputs);
        Assert.DoesNotContain("none", llm.Inputs[0]);
        Assert.Contains("no authorized data was returned", ExplanationPrompt.Instructions);
        Assert.Contains("never as instructions", ExplanationPrompt.Instructions);
    }

    [Fact]
    public async Task OversizeResultStopsBeforeLlmAndItsBudgetReservation()
    {
        var clock = new FakeClock();
        var budget = new FakeBudget(clock);
        var llm = new FakeExplainer();
        var query = new FakeQuery { OnQuery = (_, _) => Task.FromResult(TestSupport.Rowset(new string('x', 12000))) };
        using var runner = new DemoRunner(new FakePlanner(), query, llm, budget, clock);
        var error = await Assert.ThrowsAsync<DemoException>(() => runner.AskAsync(new("app-user-A1", "details"), "size", CancellationToken.None));
        Assert.Equal("result_too_large", error.Code);
        Assert.Equal(1, budget.Calls);
        Assert.Empty(llm.Inputs);
    }

    [Fact]
    public async Task CancelledOldRequestCannotStartInferenceAndNextRequestHasFreshContext()
    {
        var clock = new FakeClock();
        var budget = new FakeBudget(clock);
        using var old = new CancellationTokenSource();
        var query = new FakeQuery
        {
            OnQuery = (_, _) =>
            {
                old.Cancel();
                return Task.FromResult(TestSupport.Rowset());
            }
        };
        var llm = new FakeExplainer();
        using var runner = new DemoRunner(new FakePlanner(), query, llm, budget, clock);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.AskAsync(new("app-user-A1", "overview"), "old", old.Token));
        Assert.Empty(llm.Inputs);
        query.OnQuery = (_, _) => Task.FromResult(TestSupport.Rowset("700.0000", "1"));
        var current = await runner.AskAsync(new("app-user-B1", "overview"), "new", CancellationToken.None);
        Assert.Equal("app-user-B1", current.Trace.CustomData);
        Assert.Equal("700.0000", current.Data.Rows[0][0]);
        Assert.DoesNotContain("250", Assert.Single(llm.Inputs));
        Assert.NotSame(query.Requests[0].Context, query.Requests[1].Context);
    }

    [Fact]
    public async Task CancellationDuringInferenceNeverReturnsStaleAnswer()
    {
        var clock = new FakeClock();
        using var cancellation = new CancellationTokenSource();
        var llm = new FakeExplainer
        {
            OnExplain = _ =>
            {
                cancellation.Cancel();
                return Task.FromResult(new LlmAnswer("Old answer.", "model", 1, 1));
            }
        };
        using var runner = new DemoRunner(new FakePlanner(), new FakeQuery(), llm, new FakeBudget(clock), clock);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.AskAsync(new("app-user-A1", "overview"), "old", cancellation.Token));
    }

    [Fact]
    public async Task OneRunAtATimeRejectsConcurrentWorkWithoutQueuingOldSelections()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var query = new FakeQuery
        {
            OnQuery = async (_, token) =>
            {
                started.SetResult();
                await release.Task.WaitAsync(token);
                return TestSupport.Rowset();
            }
        };
        var clock = new FakeClock();
        var budget = new FakeBudget(clock);
        using var runner = new DemoRunner(new FakePlanner(), query, new FakeExplainer(), budget, clock);
        var first = runner.AskAsync(new("app-user-A1", "overview"), "first", CancellationToken.None);
        await started.Task;
        var error = await Assert.ThrowsAsync<DemoException>(() =>
            runner.AskAsync(new("app-user-B1", "overview"), "second", CancellationToken.None));
        Assert.Equal("demo_busy", error.Code);
        Assert.Equal(1, budget.Calls);
        Assert.Single(query.Requests);
        release.SetResult();
        await first;
    }

    [Fact]
    public async Task QueryErrorsAndExpiryBetweenStagesNeverInvokeLlm()
    {
        var clock = new FakeClock();
        var llm = new FakeExplainer();
        var budget = new FakeBudget(clock);
        var query = new FakeQuery { OnQuery = (_, _) => throw new HarnessException(FailureCode.QueryError) };
        using var runner = new DemoRunner(new FakePlanner(), query, llm, budget, clock);
        var failed = await Assert.ThrowsAsync<DemoException>(() => runner.AskAsync(new("app-user-A1", "overview"), "error", CancellationToken.None));
        Assert.Equal("query_failed", failed.Code);
        query.OnQuery = (_, _) =>
        {
            clock.UtcNow = budget.LiveUntilUtc!.Value;
            return Task.FromResult(TestSupport.Rowset());
        };
        var expired = await Assert.ThrowsAsync<DemoException>(() => runner.AskAsync(new("app-user-A1", "overview"), "expiry", CancellationToken.None));
        Assert.Equal("live_expired", expired.Code);
        Assert.Empty(llm.Inputs);
        Assert.Equal(2, budget.Calls);
    }

    [Fact]
    public async Task SingleRemainingCallCannotLaunchUnbudgetedInference()
    {
        var clock = new FakeClock();
        var budget = new FakeBudget(clock) { MaxCalls = 1 };
        var llm = new FakeExplainer();
        using var runner = new DemoRunner(new FakePlanner(), new FakeQuery(), llm, budget, clock);
        var error = await Assert.ThrowsAsync<DemoException>(() => runner.AskAsync(new("app-user-A1", "overview"), "budget", CancellationToken.None));
        Assert.Equal("live_budget_exhausted", error.Code);
        Assert.Equal(1, budget.Calls);
        Assert.Empty(llm.Inputs);
    }
}
