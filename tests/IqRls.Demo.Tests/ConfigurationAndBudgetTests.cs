using System.Text.Json.Nodes;
using IqRls.Core;
using IqRls.Demo;

namespace IqRls.Demo.Tests;

public sealed class ConfigurationAndBudgetTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "iq-rls-budget-tests", Guid.NewGuid().ToString("N"));

    public ConfigurationAndBudgetTests() => Directory.CreateDirectory(directory);

    private DemoStartup Startup(int calls = 2) => new(
        Directory.GetCurrentDirectory(),
        TestSupport.Configuration with { MaxCalls = calls },
        LiveConfiguration.Parse(TestSupport.QueryConfigurationJson),
        Path.Combine(directory, "budget.json"), "test-budget-key", true);

    [Theory]
    [InlineData("endpoint", "http://synthetic-test.openai.azure.com")]
    [InlineData("endpoint", "https://synthetic-test.openai.azure.com.evil.example")]
    [InlineData("endpoint", "https://synthetic-test.openai.azure.com/wrong")]
    [InlineData("endpoint", "https://synthetic-test.openai.azure.com/?key=test")]
    [InlineData("deployment", "../model")]
    [InlineData("tenantId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("expectedPrincipal", "not a user")]
    public void InvalidLlmTargetsAreRejected(string field, string value)
    {
        var configuration = JsonNode.Parse(TestSupport.ConfigurationJson)!;
        configuration["llm"]![field] = value;
        Assert.Equal("invalid_configuration",
            Assert.Throws<DemoException>(() => DemoConfiguration.Parse(configuration.ToJsonString())).Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void CallBudgetMustBeBounded(int calls)
    {
        var configuration = JsonNode.Parse(TestSupport.ConfigurationJson)!;
        configuration["maxCalls"] = calls;
        Assert.Throws<DemoException>(() => DemoConfiguration.Parse(configuration.ToJsonString()));
    }

    [Fact]
    public void RejectsUnapprovedStartupArguments()
    {
        Assert.Throws<DemoException>(() => DemoStartup.Read(["--urls", "http://0.0.0.0:5187"], directory));
        Assert.Throws<DemoException>(() => DemoStartup.Read(["--allow-live"], directory));
        Assert.False(DemoStartup.Read([], directory).Enabled);
    }

    [Fact]
    public void ConfigurationCannotLiveInRepositoryOrOneDrive()
    {
        var repository = Path.Combine(directory, "repository");
        Assert.Throws<DemoException>(() => DemoStartup.ExternalPath(Path.Combine(repository, "config.json"), repository));
        Assert.Throws<DemoException>(() => DemoStartup.ExternalPath(Path.Combine(directory, "OneDrive - Example", "config.json"), repository));
        Assert.Throws<DemoException>(() => DemoStartup.ExternalPath("relative.json", repository));
        Assert.Throws<DemoException>(() => DemoStartup.ExternalPath(@"\\server\share\config.json", repository));
        Assert.Equal(Path.Combine(directory, "external.json"),
            DemoStartup.ExternalPath(Path.Combine(directory, "external.json"), repository));
    }

    [Fact]
    public async Task BudgetSurvivesRestartAndCatalogDoesNotAdvertiseExhaustedAccess()
    {
        var startup = Startup();
        var clock = new FakeClock();
        using (var budget = new LiveBudget(startup, clock))
        {
            Assert.True(budget.LiveEnabled);
            await budget.BeforeCloudCallAsync(CancellationToken.None);
            await budget.BeforeCloudCallAsync(CancellationToken.None);
            Assert.Single(clock.Delays);
            Assert.Equal(TimeSpan.FromSeconds(2), clock.Delays[0]);
            Assert.False(budget.LiveEnabled);
        }
        using var restarted = new LiveBudget(startup, clock);
        Assert.False(restarted.LiveEnabled);
        Assert.Equal("live_budget_exhausted", Assert.Throws<DemoException>(restarted.EnsureAvailable).Code);
    }

    [Fact]
    public void ConcurrentHostOrCorruptLedgerFailsClosed()
    {
        var startup = Startup();
        var clock = new FakeClock();
        using (var owner = new LiveBudget(startup, clock))
        {
            owner.EnsureAvailable();
            using var contender = new LiveBudget(startup, clock);
            Assert.Equal("live_budget_unavailable", Assert.Throws<DemoException>(contender.EnsureAvailable).Code);
        }
        File.WriteAllText(startup.BudgetPath!, "not-json");
        using var corrupted = new LiveBudget(startup, clock);
        Assert.Equal("live_budget_unavailable", Assert.Throws<DemoException>(corrupted.EnsureAvailable).Code);
    }

    [Fact]
    public async Task CancellationAndExpiryDoNotReserveAnotherCall()
    {
        var clock = new FakeClock();
        using var budget = new LiveBudget(Startup(), clock);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => budget.BeforeCloudCallAsync(cancellation.Token));
        Assert.False(File.Exists(Path.Combine(directory, "budget.json")));
        clock.UtcNow = budget.LiveUntilUtc!.Value;
        Assert.False(budget.LiveEnabled);
        Assert.Equal("live_expired", Assert.Throws<DemoException>(budget.EnsureAvailable).Code);
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("invalid-calls")]
    [InlineData("missing-timestamp")]
    [InlineData("future-timestamp")]
    [InlineData("oversize")]
    public async Task RejectedLedgerStaysRejectedAcrossRepeatedAttempts(string issue)
    {
        var startup = Startup();
        var clock = new FakeClock();
        var content = new JsonObject
        {
            ["Key"] = issue == "wrong-key" ? "unapproved-config" : startup.BudgetKey,
            ["Calls"] = issue == "invalid-calls" ? -1 : 1,
            ["LastAttemptUtc"] = issue == "missing-timestamp" ? null :
                (issue == "future-timestamp" ? clock.UtcNow.AddDays(1) : clock.UtcNow).ToString("O")
        };
        var original = issue == "oversize" ? new string(' ', 4097) : content.ToJsonString();
        File.WriteAllText(startup.BudgetPath!, original);
        using var budget = new LiveBudget(startup, clock);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Equal("live_budget_unavailable", Assert.Throws<DemoException>(budget.EnsureAvailable).Code);
            Assert.Equal("live_budget_unavailable", Assert.Throws<DemoException>(() => budget.LiveEnabled).Code);
            var error = await Assert.ThrowsAsync<DemoException>(() => budget.BeforeCloudCallAsync(CancellationToken.None));
            Assert.Equal("live_budget_unavailable", error.Code);
            Assert.Equal(original, File.ReadAllText(startup.BudgetPath!));
        }
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
