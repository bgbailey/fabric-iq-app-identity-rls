using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IqRls.Core;

public sealed class ServerContext
{
    internal ServerContext(string subjectKey)
    {
        SubjectKey = subjectKey;
    }

    public string SubjectKey { get; }
    public string EntitlementVersion => HarnessContract.EntitlementVersion;
    public string ModelAlias => HarnessContract.ModelAlias;
}

public sealed record SubjectExpectation(string SubjectKey, ImmutableArray<int> ScopeKeys, decimal Total);
public sealed record ActivityExpectation(int ActivityKey, int ScopeKey, int DateKey, decimal Amount);

public static class SyntheticSubjects
{
    public static ImmutableArray<ActivityExpectation> Activities { get; } =
    [
        new(1, 1, 20260101, 100m),
        new(2, 1, 20260102, 150m),
        new(3, 2, 20260101, 40m),
        new(4, 2, 20260102, 60m),
        new(5, 3, 20260101, 700m),
        new(6, 4, 20260102, 900m)
    ];

    public static ImmutableArray<SubjectExpectation> All { get; } =
    [
        new("app-user-A1", [1], 250m),
        new("app-user-A2", [2], 100m),
        new("app-user-A3", [1, 2], 350m),
        new("app-user-B1", [3], 700m),
        new("app-user-pairs", [1, 4], 1150m),
        new("app-user-none", [], 0m)
    ];

    // Developer fixture selection, NOT browser authentication. A future server must resolve issuer/subject.
    public static ServerContext Resolve(string? subjectKey)
    {
        if (!All.Any(x => string.Equals(x.SubjectKey, subjectKey, StringComparison.Ordinal)))
            throw new HarnessException(FailureCode.UnknownSubject);
        return new ServerContext(subjectKey!);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DaxToolInput(
    [property: JsonPropertyName("query")] string Query)
{
    public static DaxToolInput Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                document.RootElement.EnumerateObject().Count() != 1)
                throw new HarnessException(FailureCode.InvalidToolInput);
            var result = JsonSerializer.Deserialize<DaxToolInput>(json);
            if (result is null) throw new HarnessException(FailureCode.InvalidToolInput);
            result.Validate();
            return result;
        }
        catch (JsonException)
        {
            throw new HarnessException(FailureCode.InvalidToolInput);
        }
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Query) || Query.Length > HarnessContract.MaxDaxCharacters ||
            Query.Contains('\0'))
            throw new HarnessException(FailureCode.InvalidToolInput);
    }
}

public sealed class DaxTool(QueryBroker broker, ServerContext context)
{
    public const string InputSchema =
        """{"type":"object","properties":{"query":{"type":"string","minLength":1,"maxLength":32768}},"required":["query"],"additionalProperties":false}""";

    public Task<QueryResult> ExecuteAsync(DaxToolInput input, CancellationToken cancellationToken = default) =>
        broker.ExecuteAsync(context, input, cancellationToken);
}
