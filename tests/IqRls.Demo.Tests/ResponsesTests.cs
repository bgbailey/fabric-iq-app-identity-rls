using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IqRls.Demo;

namespace IqRls.Demo.Tests;

public sealed class ResponsesTests
{
    [Fact]
    public async Task ExplanationUsesResponsesWithOnlyCurrentResultAndNoStoredConversation()
    {
        var prompt = ExplanationPrompt.Build(DemoCatalog.ResolveQuestion("overview"), TestSupport.Rowset());
        using var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            Assert.Equal("https://synthetic-test.openai.azure.com/openai/v1/responses", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement;
            Assert.Equal(prompt, root.GetProperty("input").GetString());
            Assert.False(root.GetProperty("store").GetBoolean());
            Assert.Equal(1024, root.GetProperty("max_output_tokens").GetInt32());
            Assert.Equal("low", root.GetProperty("reasoning").GetProperty("effort").GetString());
            Assert.Equal(ExplanationPrompt.Instructions, root.GetProperty("instructions").GetString());
            foreach (var name in new[] { "tools", "previous_response_id", "temperature", "top_p" })
                Assert.False(root.TryGetProperty(name, out _));
            return new(HttpStatusCode.OK) { Content = new StringContent(TestSupport.CompletedResponse) };
        });
        using var http = new HttpClient(handler);
        var tokens = new TestTokens();
        var client = new ResponsesExplainer(http, tokens, TestSupport.Configuration.Llm);
        var answer = await client.ExplainAsync(prompt, CancellationToken.None);
        Assert.Equal("The returned total is 250 across 2 records.", answer.Text);
        Assert.Equal("synthetic-actual-model-version", answer.Model);
        Assert.Equal(123, answer.InputTokens);
        Assert.Equal(37, answer.OutputTokens);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(1, tokens.Calls);
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("failed")]
    [InlineData("in_progress")]
    public void IncompleteResultsNeverBecomeAnswers(string status)
    {
        var root = JsonNode.Parse(TestSupport.CompletedResponse)!;
        root["status"] = status;
        Assert.Equal("llm_incomplete",
            Assert.Throws<DemoException>(() => ResponsesExplainer.ParseResponse(Encoding.UTF8.GetBytes(root.ToJsonString()))).Code);
    }

    [Theory]
    [InlineData("refusal")]
    [InlineData("missing-usage")]
    [InlineData("function-call")]
    [InlineData("empty-text")]
    [InlineData("negative-tokens")]
    public void InvalidModelResponsesFailClosed(string issue)
    {
        var root = JsonNode.Parse(TestSupport.CompletedResponse)!;
        if (issue == "refusal") root["output"]![1]!["content"]![0]!["type"] = "refusal";
        if (issue == "missing-usage") root.AsObject().Remove("usage");
        if (issue == "function-call") root["output"]![1]!["type"] = "function_call";
        if (issue == "empty-text") root["output"]![1]!["content"]![0]!["text"] = "";
        if (issue == "negative-tokens") root["usage"]!["input_tokens"] = -1;
        Assert.Throws<DemoException>(() => ResponsesExplainer.ParseResponse(Encoding.UTF8.GetBytes(root.ToJsonString())));
    }

    [Fact]
    public async Task ServiceErrorsDoNotLeakBodiesOrAutomaticallyRetry()
    {
        using var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("private-service-body")
        }));
        using var http = new HttpClient(handler);
        var client = new ResponsesExplainer(http, new TestTokens(), TestSupport.Configuration.Llm);
        var error = await Assert.ThrowsAsync<DemoException>(() => client.ExplainAsync("{}", CancellationToken.None));
        Assert.Equal("llm_http_failure", error.Code);
        Assert.DoesNotContain("private", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("wrong-tenant")]
    [InlineData("wrong-user")]
    [InlineData("expired")]
    [InlineData("missing-user")]
    public void WrongCliIdentityIsRejectedBeforeInference(string issue)
    {
        var configuration = TestSupport.Configuration.Llm;
        var now = new FakeClock().UtcNow;
        var payload = new Dictionary<string, object>
        {
            ["tid"] = issue == "wrong-tenant" ? Guid.NewGuid().ToString("D") : configuration.TenantId.ToString("D"),
            ["exp"] = (issue == "expired" ? now.AddSeconds(-1) : now.AddMinutes(5)).ToUnixTimeSeconds(),
            ["upn"] = issue == "wrong-user" ? "different@example.invalid" : configuration.ExpectedPrincipal
        };
        if (issue == "missing-user") payload.Remove("upn");
        var body = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal("llm_auth_failed", Assert.Throws<DemoException>(() =>
            PinnedAzureCliTokens.ValidateIdentity($"synthetic.{body}.synthetic", configuration, now)).Code);
    }
}
