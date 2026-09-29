using IqRls.Core;

namespace IqRls.Demo;

public sealed class LiveDemoPlanner : IDemoPlanner, IDisposable
{
    public const string MetadataMode = DemoCatalog.MetadataMode;
    public const string GuidanceCommit = "6c11ad58c25992e5d1435ce7cd80d217d5598a31";
    private readonly DemoStartup startup;
    private readonly ILiveBudget budget;
    private readonly IDemoClock clock;
    private readonly HttpClient http;
    private readonly Func<IAccessTokenSource>? iqTokensFactory;
    private readonly IAccessTokenSource? modelTokens;
    private readonly bool ownsHttp;

    public LiveDemoPlanner(DemoStartup startup, ILiveBudget budget, IDemoClock clock)
    {
        this.startup = startup;
        this.budget = budget;
        this.clock = clock;
        http = CloudHttp.Create();
        ownsHttp = true;
    }

    public LiveDemoPlanner(DemoStartup startup, ILiveBudget budget, IDemoClock clock, HttpClient http,
        Func<IAccessTokenSource> iqTokensFactory, IAccessTokenSource modelTokens)
    {
        this.startup = startup;
        this.budget = budget;
        this.clock = clock;
        this.http = http;
        this.iqTokensFactory = iqTokensFactory;
        this.modelTokens = modelTokens;
    }

    public async Task<QueryPlan> PlanAsync(DemoQuestion question, IExecutionObserver observer, CancellationToken cancellationToken)
    {
        if (!startup.Enabled) throw new DemoException("live_not_enabled", "Live planning is disabled.");
        budget.EnsureAvailable();
        var config = IqConfiguration.Validate(startup);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        IAccessTokenSource? tokens = null;
        try
        {
            var authDetails = new Dictionary<string, string>
            {
                ["credential"] = "InteractiveBrowserCredential: silent cached delegated authentication only",
                ["audience"] = IqConfiguration.Audience,
                ["scopes"] = "Item.Read.All, Item.Execute.All, Dataset.Read.All",
                ["tenant"] = "MCAPS DEV; matches query and model tenant",
                ["guidanceCommit"] = GuidanceCommit,
                ["guidancePaths"] = "skills/fabriciq/SKILL.md; common/COMMON-CORE.md; common/COMMON-CLI.md",
                ["documentation"] = "https://learn.microsoft.com/en-us/fabric/iq/connectors/fabric-iq-mcp",
                ["policy"] = "Synthetic metadata only; no native ExecuteQuery/ValueSearch, custom instructions or verified answer reuse."
            };
            await observer.EmitAsync("iq-auth", "started", "Acquire delegated Power BI token",
                "MCAPS DEV delegated metadata user", authDetails, deadline.Token);
            tokens = iqTokensFactory?.Invoke() ?? new PinnedIqDelegatedTokens(startup, clock);
            var token = await tokens.GetTokenAsync(deadline.Token).ConfigureAwait(false);
            PinnedIqDelegatedTokens.ValidateIdentity(token, config, clock.UtcNow);
            await observer.EmitAsync("iq-auth", "completed", "Acquire delegated Power BI token",
                "MCAPS DEV delegated metadata user", authDetails, deadline.Token);
            var schema = await new FabricIqSchemaClient(http, budget).ReadSchemaAsync(startup.QueryConfiguration!.DatasetId,
                token, observer, deadline.Token).ConfigureAwait(false);
            var generated = await new ResponsesDaxGenerator(http, modelTokens ?? new PinnedAzureCliTokens(startup.Configuration!.Llm, clock),
                startup.Configuration!.Llm, budget).GenerateAsync(question, schema, observer, deadline.Token).ConfigureAwait(false);
            return new(generated.Query, schema.Hash, schema.Json, generated.Model, generated.InputTokens, generated.OutputTokens, MetadataMode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DemoException("planning_timeout", "Live schema discovery and DAX generation exceeded the planning time limit.", 504);
        }
        finally { (tokens as IDisposable)?.Dispose(); }
    }

    public void Dispose()
    {
        if (ownsHttp) http.Dispose();
    }
}
