using System.Globalization;
using System.Text.Json;

namespace ModelDefinition;

internal sealed record ScopeRow(long ScopeKey, string Customer, string Product);
internal sealed record ActivityRow(long ActivityKey, long ScopeKey, long DateKey, decimal Amount);
internal sealed record AccessRow(string SubjectKey, long ScopeKey);
internal sealed record CalendarFixture(DateOnly Start, DateOnly End, int ExpectedRows);
internal sealed record BaselineFixture(decimal TotalAmount, int ActivityCount, long[] ScopeKeys, int EntitlementRows);
internal sealed record IdentityCase(
    string Name, string? CustomData, long[] ScopeKeys, long[] ActivityKeys, int RowCount,
    int EntitlementRows, decimal? TotalAmount, int? ActivityCount, string? Role);

internal sealed record Fixture(
    int SchemaVersion, string ModelAlias, string EntitlementVersion, string Classification, bool LiveValidated,
    ScopeRow[] Scopes, ActivityRow[] Activities, AccessRow[] UserAccess,
    CalendarFixture Calendar, BaselineFixture Baseline, IdentityCase[] Cases)
{
    internal static Fixture Read(byte[] bytes) =>
        JsonSerializer.Deserialize<Fixture>(bytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException("Empty fixture.");

    internal void Validate()
    {
        Check.That(SchemaVersion == 1 && Classification == "synthetic-only" && !LiveValidated,
            "Fixture must remain synthetic and not live-validated.");
        Check.That(ModelAlias == "synthetic-rls-v1" && EntitlementVersion == "synthetic-v1",
            "Fixture must match the broker's pinned logical model and entitlement versions.");
        Check.Sequence(Scopes, new ScopeRow[]
        {
            new(1, "A", "Home"), new(2, "A", "Auto"), new(3, "B", "Home"), new(4, "B", "Auto")
        }, "Exact scope pairs");
        Check.Sequence(Activities, new ActivityRow[]
        {
            new(1, 1, 20260101, 100), new(2, 1, 20260102, 150),
            new(3, 2, 20260101, 40), new(4, 2, 20260102, 60),
            new(5, 3, 20260101, 700), new(6, 4, 20260102, 900)
        }, "Exact activity seeds");
        Check.Sequence(UserAccess, new AccessRow[]
        {
            new("app-user-A1", 1), new("app-user-A2", 2),
            new("app-user-A3", 1), new("app-user-A3", 2), new("app-user-B1", 3),
            new("app-user-pairs", 1), new("app-user-pairs", 4)
        }, "Exact entitlement seeds");
        Check.That(Calendar == new CalendarFixture(new(2026, 1, 1), new(2026, 1, 31), 31),
            "Expected contiguous January calendar.");
        Check.That(Activities.Sum(row => row.Amount) == 1950 && Baseline.TotalAmount == 1950
            && Baseline.ActivityCount == 6 && Baseline.EntitlementRows == 7, "Baseline totals");
        Check.Sequence(Baseline.ScopeKeys, new long[] { 1, 2, 3, 4 }, "Baseline scopes");
        Check.Sequence(Cases.Select(item => (item.Name, item.CustomData, item.Role)), new[]
        {
            ("A1", "app-user-A1", (string?)null), ("A2", "app-user-A2", null),
            ("A3", "app-user-A3", null), ("B1", "app-user-B1", null),
            ("pairs", "app-user-pairs", null), ("none", "app-user-none", null),
            ("missing", (string?)null, null), ("empty", "", null),
            ("unknown", "app-user-unknown", null), ("case-variant", "APP-USER-A1", null),
            ("trailing-space", "app-user-A1 ", null), ("metadata-only", "app-user-A1", "MetadataOnly")
        }, "Required positive/negative identity cases");

        foreach (var testCase in Cases)
        {
            var grants = UserAccess.Where(row => testCase.Role != "MetadataOnly"
                && !string.IsNullOrEmpty(testCase.CustomData)
                && string.Equals(row.SubjectKey, testCase.CustomData, StringComparison.Ordinal)).ToArray();
            var scopes = grants.Select(row => row.ScopeKey).Distinct().Order().ToArray();
            var rows = Activities.Where(row => scopes.Contains(row.ScopeKey)).ToArray();
            Check.Sequence(scopes, testCase.ScopeKeys, $"{testCase.Name} scopes");
            Check.Sequence(rows.Select(row => row.ActivityKey), testCase.ActivityKeys, $"{testCase.Name} activity keys");
            Check.That(rows.Length == testCase.RowCount && grants.Length == testCase.EntitlementRows,
                $"{testCase.Name} visible row counts");
            Check.That(testCase.TotalAmount == (rows.Length == 0 ? null : rows.Sum(row => row.Amount))
                && testCase.ActivityCount == (rows.Length == 0 ? null : rows.Length),
                $"{testCase.Name} raw measure expectations (empty is BLANK)");
        }
    }

    internal Dictionary<string, string> InlineExpressions()
    {
        static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
        static string Text(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        static string Row(params string[] values) => "{" + string.Join(", ", values) + "}";
        static string Table(string type, IEnumerable<string> rows) =>
            "#table(\n    type table [" + type + "],\n    {\n        "
            + string.Join(",\n        ", rows) + "\n    }\n)";

        return new(StringComparer.Ordinal)
        {
            ["Scope"] = Table("#\"Scope Key\" = Int64.Type, Customer = text, Product = text",
                Scopes.Select(row => Row(Number(row.ScopeKey), Text(row.Customer), Text(row.Product)))),
            ["Activity"] = Table("#\"Activity Key\" = Int64.Type, #\"Scope Key\" = Int64.Type, #\"Date Key\" = Int64.Type, Amount = Currency.Type",
                Activities.Select(row => Row(Number(row.ActivityKey), Number(row.ScopeKey), Number(row.DateKey),
                    row.Amount.ToString("0.####", CultureInfo.InvariantCulture)))),
            ["User Access"] = Table("#\"Subject Key\" = text, #\"Scope Key\" = Int64.Type",
                UserAccess.Select(row => Row(Text(row.SubjectKey), Number(row.ScopeKey)))),
            ["Date"] = Table("#\"Date Key\" = Int64.Type, Date = datetime, Year = Int64.Type, Month = Int64.Type, Day = Int64.Type",
                Enumerable.Range(0, Calendar.ExpectedRows).Select(offset =>
                {
                    var day = Calendar.Start.AddDays(offset);
                    return Row(day.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                        $"#datetime({Number(day.Year)}, {Number(day.Month)}, {Number(day.Day)}, 0, 0, 0)",
                        Number(day.Year), Number(day.Month), Number(day.Day));
                }))
        };
    }
}

internal static class Check
{
    internal static void That(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }

    internal static void Sequence<T>(IEnumerable<T> actual, IEnumerable<T> expected, string message) =>
        That(actual.SequenceEqual(expected), message);

    internal static string Expression(string value) =>
        string.Join("\n", value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => line.Trim())).Trim();
}
