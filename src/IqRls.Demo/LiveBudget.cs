using System.Text.Json;

namespace IqRls.Demo;

public interface IDemoClock
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class DemoClock : IDemoClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

public interface ILiveBudget
{
    bool LiveEnabled { get; }
    DateTimeOffset? LiveUntilUtc { get; }
    void EnsureAvailable();
    Task BeforeCloudCallAsync(CancellationToken cancellationToken);
}

public sealed class LiveBudget(DemoStartup startup, IDemoClock clock) : ILiveBudget, IDisposable
{
    private sealed record Ledger(string Key, int Calls, DateTimeOffset? LastAttemptUtc);
    private FileStream? file;
    private Ledger? ledger;
    private readonly object sync = new();

    public bool LiveEnabled
    {
        get
        {
            lock (sync)
            {
                if (!startup.Enabled || clock.UtcNow >= startup.Configuration!.LiveUntilUtc) return false;
                OpenLedger();
                return ledger!.Calls < startup.Configuration.MaxCalls;
            }
        }
    }
    public DateTimeOffset? LiveUntilUtc => startup.Configuration?.LiveUntilUtc;

    public void EnsureAvailable()
    {
        lock (sync) EnsureAvailableLocked();
    }

    private void EnsureAvailableLocked()
    {
        if (!startup.Enabled)
            throw new DemoException("live_not_enabled", "Live execution is disabled. An approved external configuration and --allow-live are both required.");
        if (clock.UtcNow >= startup.Configuration!.LiveUntilUtc)
            throw new DemoException("live_expired", "The approved live window has ended.");
        OpenLedger();
        if (ledger!.Calls >= startup.Configuration.MaxCalls)
            throw new DemoException("live_budget_exhausted", "The approved cloud-call budget has been used.", 429);
    }

    public async Task BeforeCloudCallAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TimeSpan delay;
        lock (sync)
        {
            EnsureAvailableLocked();
            delay = ledger!.LastAttemptUtc is { } last ? last.AddSeconds(2) - clock.UtcNow : TimeSpan.Zero;
        }
        if (delay > TimeSpan.Zero) await clock.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            EnsureAvailableLocked();
            // Persist before transport. Failed/cancelled attempts are not refunded.
            ledger = ledger! with { Calls = ledger.Calls + 1, LastAttemptUtc = clock.UtcNow };
            SaveLedger();
        }
    }

    private void OpenLedger()
    {
        if (file is not null) return;
        FileStream? opened = null;
        try
        {
            opened = new FileStream(startup.BudgetPath!, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Ledger validated;
            if (opened.Length == 0)
            {
                validated = new(startup.BudgetKey!, 0, null);
                SaveLedger(opened, validated);
            }
            else
            {
                if (opened.Length > 4096) throw new InvalidDataException();
                var candidate = JsonSerializer.Deserialize<Ledger>(opened);
                if (candidate is null || candidate.Key != startup.BudgetKey ||
                    candidate.Calls is < 0 or > 100 || (candidate.Calls > 0 && candidate.LastAttemptUtc is null) ||
                    candidate.LastAttemptUtc > clock.UtcNow)
                    throw new InvalidDataException();
                validated = candidate;
            }
            file = opened;
            ledger = validated;
            opened = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            throw new DemoException("live_budget_unavailable", "The persistent budget is locked, changed or unreadable. No cloud call was made.");
        }
        finally { opened?.Dispose(); }
    }

    private void SaveLedger() => SaveLedger(file!, ledger!);

    private static void SaveLedger(FileStream target, Ledger value)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
            target.Position = 0;
            target.Write(bytes);
            target.SetLength(bytes.Length);
            target.Flush(flushToDisk: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new DemoException("live_budget_unavailable", "The persistent budget could not be saved. No cloud call was made.");
        }
    }

    public void Dispose() => file?.Dispose();
}
