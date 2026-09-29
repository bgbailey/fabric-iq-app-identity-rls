using System.Collections.Immutable;

namespace IqRls.Demo;

public sealed record DemoIdentity(string Id, string Label, string Scope);
public sealed record DemoQuestion(string Id, string Text, string Purpose);

public static class DemoCatalog
{
    public const string MetadataMode = "live-fabric-iq-schema";
    public const string IdentityNotice = "The user selector simulates trusted application identity. It is not a login.";
    public const string EvidenceNote = "Live IQ schema, generated DAX, scoped execution and explanation demonstrated on synthetic cases. The separate deterministic harness passed 42 RLS checks. Educational proof, not production certification.";

    public static ImmutableArray<DemoIdentity> Identities { get; } =
    [
        new("app-user-A1", "Customer A - Home", "A / Home"),
        new("app-user-A2", "Customer A - Auto", "A / Auto"),
        new("app-user-A3", "Customer A - All products", "A / Home + A / Auto"),
        new("app-user-B1", "Customer B - Home", "B / Home"),
        new("app-user-pairs", "Cross-customer pair", "A / Home + B / Auto"),
        new("app-user-none", "No data access", "No authorized scopes")
    ];

    public static ImmutableArray<DemoQuestion> Questions { get; } =
    [
        new("overview", "What is my total activity amount and how many records do I have?",
            "Same question, model-enforced user scope"),
        new("breakdown", "Break down my activity by customer and product.",
            "Show exact customer/product pairs"),
        new("daily", "Compare my total activity amount and record count on January 1 and January 2, 2026.",
            "Narrate a daily comparison from authorized rows"),
        new("details", "Show the activity records behind my total.",
            "Inspect the actual rows returned by the semantic model"),
        new("customer-b-home", "Show Customer B's Home activity.",
            "A business filter cannot expand RLS access")
    ];

    public static DemoQuestion ResolveQuestion(string? id) =>
        Questions.FirstOrDefault(q => string.Equals(q.Id, id, StringComparison.Ordinal))
        ?? throw new DemoException("unknown_question", "Choose a prepared question.", 400);

    public static void ValidateUser(string? id)
    {
        if (!Identities.Any(i => string.Equals(i.Id, id, StringComparison.Ordinal)))
            throw new DemoException("unknown_user", "Choose a known synthetic demo user.", 400);
    }
}
