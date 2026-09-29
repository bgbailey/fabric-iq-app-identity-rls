namespace IqRls.Demo;

public sealed record ExecutionEvent(int Sequence, string RequestId, string Step, string Status,
    string Operation, string Identity, DateTimeOffset TimestampUtc, long ElapsedMs,
    IReadOnlyDictionary<string, string> Details);

public interface IExecutionObserver
{
    ValueTask EmitAsync(string step, string status, string operation, string identity,
        IReadOnlyDictionary<string, string>? details, CancellationToken cancellationToken);
}

public sealed record QueryPlan(string Query, string SchemaHash, string SchemaSummary, string Model,
    int InputTokens, int OutputTokens, string MetadataMode);

public interface IDemoPlanner
{
    Task<QueryPlan> PlanAsync(DemoQuestion question, IExecutionObserver observer, CancellationToken cancellationToken);
}
