using System.Diagnostics;
using System.Text.Json;

namespace IqRls.Demo;

public sealed class ExecutionSession(string requestId, Func<ExecutionEvent, CancellationToken, ValueTask>? sink = null,
    TextWriter? log = null) : IExecutionObserver
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private readonly TextWriter writer = log ?? Console.Out;
    private int sequence;
    private string currentStep = "identity";
    private string currentOperation = "Resolve synthetic application identity";
    private string currentIdentity = "Application user (not Entra)";
    private string currentStatus = "started";

    public async ValueTask EmitAsync(string step, string status, string operation, string identity,
        IReadOnlyDictionary<string, string>? details, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        currentStep = step;
        currentOperation = operation;
        currentIdentity = identity;
        currentStatus = status;
        var observation = new ExecutionEvent(++sequence, requestId, step, status, operation, identity,
            DateTimeOffset.UtcNow, watch.ElapsedMilliseconds, details ?? new Dictionary<string, string>());
        // Console logs contain lifecycle metadata, never cloud bodies, schema, DAX, rows or credentials.
        await writer.WriteLineAsync(JsonSerializer.Serialize(new
        {
            observation.Sequence, observation.RequestId, observation.Step, observation.Status,
            observation.TimestampUtc, observation.ElapsedMs
        }, JsonOptions));
        if (sink is not null) await sink(observation, cancellationToken);
    }

    public ValueTask FailAsync(string code, CancellationToken cancellationToken) =>
        EmitAsync(currentStatus == "started" ? currentStep : "request", "failed",
            currentStatus == "started" ? currentOperation : "Request stopped", currentIdentity,
            new Dictionary<string, string> { ["errorCode"] = code }, cancellationToken);

    public void RecordDisconnect() => writer.WriteLine(JsonSerializer.Serialize(new
    {
        sequence = ++sequence, requestId, step = currentStep, status = "cancelled",
        timestampUtc = DateTimeOffset.UtcNow, elapsedMs = watch.ElapsedMilliseconds
    }, JsonOptions));
}
