using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using IqRls.Core;

namespace IqRls.Tests;

public sealed class BrokerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("app-user-a1")]
    [InlineData("unknown")]
    [InlineData("app-user-A1 ")]
    public void MissingOrUnknownSubjectFailsClosed(string? subject)
    {
        Assert.Equal(FailureCode.UnknownSubject, Assert.Throws<HarnessException>(() => SyntheticSubjects.Resolve(subject)).Code);
    }

    [Fact]
    public void ServerContextHasNoPublicConstructorOrWritableProperties()
    {
        Assert.Empty(typeof(ServerContext).GetConstructors());
        Assert.All(typeof(ServerContext).GetProperties(), p => Assert.Null(p.SetMethod));
        var context = ServerContext.ForUserKey("external-user-1");
        Assert.Equal("external-user-1", context.SubjectKey);
        Assert.Equal(HarnessContract.ModelAlias, context.ModelAlias);
        Assert.Equal(HarnessContract.EntitlementVersion, context.EntitlementVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" app-user-A1")]
    [InlineData("app-user-A1 ")]
    [InlineData("bad\rkey")]
    public void ForUserKeyRejectsInvalidUserKeys(string? userKey)
    {
        Assert.Equal(FailureCode.UnknownSubject,
            Assert.Throws<HarnessException>(() => ServerContext.ForUserKey(userKey!)).Code);
    }

    [Theory]
    [InlineData("""{"query":"EVALUATE ROW(\"X\",1)","roles":[]}""")]
    [InlineData("""{"query":"x","customData":"app-user-pairs"}""")]
    [InlineData("""{"query":"x","effectiveUsername":"admin@example.invalid"}""")]
    [InlineData("""{"query":"x","datasetId":"override"}""")]
    [InlineData("""{"query":"x","modelAlias":"override"}""")]
    [InlineData("""{"query":"x","subjectKey":"app-user-A3"}""")]
    [InlineData("""{"query":"x","query":"y"}""")]
    [InlineData("""{"query":""}""")]
    [InlineData("""{"query":null}""")]
    [InlineData("""{}""")]
    [InlineData("""[]""")]
    public void ToolInputRejectsOverridesAndInvalidQuery(string json)
    {
        Assert.Equal(FailureCode.InvalidToolInput, Assert.Throws<HarnessException>(() => DaxToolInput.Parse(json)).Code);
    }

    [Fact]
    public void ToolSchemaHasOnlyQuery()
    {
        using var document = JsonDocument.Parse(DaxTool.InputSchema);
        Assert.Equal(["query"], document.RootElement.GetProperty("properties").EnumerateObject().Select(x => x.Name));
        Assert.False(document.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Single(typeof(DaxToolInput).GetProperties());
    }

    [Fact]
    public async Task ConcurrentRequestsAlwaysCarryTheirOwnContextRoleAndAuthorization()
    {
        var captured = new ConcurrentBag<(string Query, string Subject, string Auth)>();
        using var handler = new TestData.Handler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(TestData.Configuration.Endpoint, request.RequestUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            Assert.Equal(["customData", "query", "queryTimeout", "resultSetRowCountLimit", "roles"],
                root.EnumerateObject().Select(p => p.Name).Order().ToArray());
            Assert.Equal(["ExternalAppScope"], root.GetProperty("roles").EnumerateArray().Select(v => v.GetString()));
            Assert.Equal(30, root.GetProperty("queryTimeout").GetInt32());
            Assert.Equal(1000, root.GetProperty("resultSetRowCountLimit").GetInt32());
            captured.Add((root.GetProperty("query").GetString()!, root.GetProperty("customData").GetString()!,
                request.Headers.Authorization!.Parameter!));
            await Task.Yield();
            return TestData.Response(TestData.Scalar());
        });
        using var client = new HttpClient(handler);
        var broker = new QueryBroker(client, new TestData.Tokens(), TestData.Configuration);
        await Task.WhenAll(SyntheticSubjects.All.Select(s =>
            broker.ExecuteAsync(ServerContext.ForUserKey(s.SubjectKey), new(RlsVerifier.TotalQuery))));
        Assert.Equal(6, captured.Count);
        Assert.Equal(SyntheticSubjects.All.Select(s => s.SubjectKey).Order(), captured.Select(c => c.Subject).Order());
        Assert.All(captured, item =>
        {
            Assert.Equal(RlsVerifier.TotalQuery, item.Query);
            Assert.Equal("synthetic-token-not-a-credential", item.Auth);
        });
        Assert.Null(client.DefaultRequestHeaders.Authorization);
        Assert.Empty(client.DefaultRequestHeaders);
    }


    [Fact]
    public async Task EffectiveUsernameModeCarriesUsernameRoleWithoutCustomData()
    {
        using var handler = new TestData.Handler(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            Assert.Equal(["effectiveUsername", "query", "queryTimeout", "resultSetRowCountLimit", "roles"],
                root.EnumerateObject().Select(p => p.Name).Order().ToArray());
            Assert.Equal([HarnessContract.UsernameRole], root.GetProperty("roles").EnumerateArray().Select(v => v.GetString()));
            Assert.Equal("external-user-1", root.GetProperty("effectiveUsername").GetString());
            return TestData.Response(TestData.Scalar());
        });
        using var client = new HttpClient(handler);
        var broker = new QueryBroker(client, new TestData.Tokens(),
            TestData.Configuration.WithIdentityMode(QueryIdentityMode.EffectiveUsername));
        await broker.ExecuteAsync(ServerContext.ForUserKey("external-user-1"), new(RlsVerifier.TotalQuery));
    }

    [Fact]
    public async Task InvalidContextDoesNotRequestATokenOrUseTransport()
    {
        var tokens = new TestData.Tokens();
        using var handler = new TestData.Handler((_, _) => throw new InvalidOperationException());
        using var client = new HttpClient(handler);
        var broker = new QueryBroker(client, tokens, TestData.Configuration);
        var error = await Assert.ThrowsAsync<HarnessException>(() => broker.ExecuteAsync(null!, new("x")));
        Assert.Equal(FailureCode.InvalidContext, error.Code);
        Assert.Equal(0, tokens.Calls);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task InvalidDaxDoesNotRequestAToken()
    {
        var tokens = new TestData.Tokens();
        using var handler = new TestData.Handler((_, _) => throw new InvalidOperationException());
        using var client = new HttpClient(handler);
        var broker = new QueryBroker(client, tokens, TestData.Configuration);
        var error = await Assert.ThrowsAsync<HarnessException>(() =>
            broker.ExecuteAsync(SyntheticSubjects.Resolve("app-user-A1"), new(new string('a', 32769))));
        Assert.Equal(FailureCode.InvalidToolInput, error.Code);
        Assert.Equal(0, tokens.Calls);
    }

    [Fact]
    public async Task HttpErrorsNeverExposeResponseBodiesOrCredentials()
    {
        using var handler = new TestData.Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("SENSITIVE_BODY_SENTINEL")
        }));
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<HarnessException>(() => new QueryBroker(client,
            new TestData.Tokens("SENSITIVE_TOKEN_SENTINEL"), TestData.Configuration)
            .ExecuteAsync(SyntheticSubjects.Resolve("app-user-A1"), new("EVALUATE 'Scope'")));
        Assert.Equal(FailureCode.HttpFailure, error.Code);
        Assert.DoesNotContain("SENSITIVE", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("")]
    [InlineData("SECRET\r\nX-Evil: 1")]
    public async Task InvalidTokenNeverReachesTransport(string token)
    {
        using var handler = new TestData.Handler((_, _) => throw new InvalidOperationException());
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<HarnessException>(() =>
            new QueryBroker(client, new TestData.Tokens(token), TestData.Configuration)
                .ExecuteAsync(SyntheticSubjects.Resolve("app-user-A1"), new("x")));
        Assert.Equal(FailureCode.AuthenticationFailed, error.Code);
        Assert.Equal(0, handler.Calls);
        Assert.DoesNotContain("SECRET", error.ToString());
    }

    private sealed class FailingTokens : IAccessTokenSource
    {
        public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("AUTH_SECRET_SENTINEL");
    }

    [Fact]
    public async Task AuthenticationAdapterExceptionIsSanitized()
    {
        using var handler = new TestData.Handler((_, _) => throw new InvalidOperationException());
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<HarnessException>(() =>
            new QueryBroker(client, new FailingTokens(), TestData.Configuration)
                .ExecuteAsync(SyntheticSubjects.Resolve("app-user-A1"), new("x")));
        Assert.Equal(FailureCode.AuthenticationFailed, error.Code);
        Assert.DoesNotContain("AUTH_SECRET_SENTINEL", error.ToString());
        Assert.Null(error.InnerException);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ErrorAfterDataUnderHttp200RejectsWholeResponse()
    {
        using var handler = new TestData.Handler((_, _) =>
            Task.FromResult(TestData.Response(TestData.Scalar().Concat(TestData.Error()).ToArray())));
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<HarnessException>(() =>
            new QueryBroker(client, new TestData.Tokens(), TestData.Configuration)
                .ExecuteAsync(SyntheticSubjects.Resolve("app-user-A1"), new("x")));
        Assert.Equal(FailureCode.QueryError, error.Code);
        Assert.DoesNotContain("SENSITIVE", error.ToString());
    }
}
