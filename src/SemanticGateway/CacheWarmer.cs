using SemanticGateway.Chat;
using SemanticGateway.Fabric;

namespace SemanticGateway;

/// <summary>
/// Keeps the Fabric IQ schema and the Power BI and Azure OpenAI tokens warm, so a user's question
/// never waits for any of them, even when the gateway has been idle for hours.
/// </summary>
public sealed class CacheWarmer(SchemaCache schema, PowerBiAppIdentity powerBi, ChatAgent agent, ILogger<CacheWarmer> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            await WarmAsync("Fabric IQ schema", () => schema.GetAsync(stoppingToken));
            await WarmAsync("Power BI token", () => powerBi.GetTokenAsync(stoppingToken));
            await WarmAsync("Azure OpenAI token", () => agent.WarmUpAsync(stoppingToken));
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));

        async Task WarmAsync(string what, Func<Task> warm)
        {
            try { await warm(); }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogWarning("Keeping the {What} warm failed: {Message}", what, error.Message);
            }
        }
    }
}
