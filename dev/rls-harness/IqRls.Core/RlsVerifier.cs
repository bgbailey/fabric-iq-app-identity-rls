using System.Collections.Immutable;
using System.Globalization;

namespace IqRls.Core;

public sealed record VerificationCase(string Subject, string Check, bool Passed);
public sealed record VerificationReport(string PrincipalMode, bool LiveExecuted, ImmutableArray<VerificationCase> Checks)
{
    public bool Passed => Checks.All(c => c.Passed);
}

public static class RlsVerifier
{
    public const string TotalQuery =
        """EVALUATE ROW("Total", COALESCE([Total Amount], 0), "Count", COALESCE([Activity Count], 0))""";
    public const string ScopeQuery =
        """EVALUATE SELECTCOLUMNS('Scope', "ScopeKey", 'Scope'[Scope Key], "Customer", 'Scope'[Customer], "Product", 'Scope'[Product]) ORDER BY [ScopeKey]""";
    public const string ActivityQuery =
        """EVALUATE SELECTCOLUMNS('Activity', "ActivityKey", 'Activity'[Activity Key], "ScopeKey", 'Activity'[Scope Key], "DateKey", 'Activity'[Date Key], "Amount", 'Activity'[Amount]) ORDER BY [ActivityKey]""";
    public const string AllQuery =
        """EVALUATE ROW("Total", COALESCE(CALCULATE([Total Amount], ALL('Scope'), ALL('Activity'), ALL('User Access')), 0), "Count", COALESCE(CALCULATE([Activity Count], ALL('Scope'), ALL('Activity'), ALL('User Access')), 0))""";
    public const string RemoveFiltersQuery =
        """EVALUATE ROW("Total", COALESCE(CALCULATE([Total Amount], REMOVEFILTERS()), 0), "Count", COALESCE(CALCULATE([Activity Count], REMOVEFILTERS()), 0))""";
    public const string UserAccessQuery =
        """EVALUATE SELECTCOLUMNS(ALL('User Access'), "SubjectKey", 'User Access'[Subject Key], "ScopeKey", 'User Access'[Scope Key]) ORDER BY [SubjectKey], [ScopeKey]""";

    public static string DeniedQuery(SubjectExpectation subject)
    {
        var denied = Enumerable.Range(1, 4).First(k => !subject.ScopeKeys.Contains(k));
        return $$"""EVALUATE ROW("DeniedCount", COALESCE(COUNTROWS(FILTER(ALL('Activity'), 'Activity'[Scope Key] = {{denied}})), 0))""";
    }

    public static async Task<VerificationReport> VerifyAsync(
        Func<ServerContext, DaxToolInput, CancellationToken, Task<QueryResult>> execute,
        bool liveExecuted,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? betweenQueries = null)
    {
        var checks = ImmutableArray.CreateBuilder<VerificationCase>();
        foreach (var subject in SyntheticSubjects.All)
        {
            var context = SyntheticSubjects.Resolve(subject.SubjectKey);
            async Task<QueryResult> Run(string dax)
            {
                if (betweenQueries is not null) await betweenQueries(cancellationToken).ConfigureAwait(false);
                return await execute(context, new(dax), cancellationToken).ConfigureAwait(false);
            }
            var totals = await Run(TotalQuery);
            var scopes = await Run(ScopeQuery);
            var activity = await Run(ActivityQuery);
            var all = await Run(AllQuery);
            var removed = await Run(RemoveFiltersQuery);
            var denied = await Run(DeniedQuery(subject));
            var access = await Run(UserAccessQuery);
            var activityValid = ActivityMatches(activity, subject);
            checks.Add(new(subject.SubjectKey, "same-unfiltered-total", TotalsMatch(totals, subject)));
            checks.Add(new(subject.SubjectKey, "direct-scope-projection", ScopeMatches(scopes, subject)));
            checks.Add(new(subject.SubjectKey, "direct-activity-projection", activityValid));
            checks.Add(new(subject.SubjectKey, "all-cannot-expand-security", TotalsMatch(all, subject)));
            checks.Add(new(subject.SubjectKey, "removefilters-cannot-expand-security", TotalsMatch(removed, subject)));
            checks.Add(new(subject.SubjectKey, "explicit-denied-scope", Columns(denied, "DeniedCount") &&
                denied.Rows.Length == 1 && Number(denied.Rows[0][0]) == 0));
            checks.Add(new(subject.SubjectKey, "entitlement-enumeration", AccessMatches(access, subject)));
        }
        return new(liveExecuted ? "application-certificate" : "mock-transport-not-model-RLS",
            liveExecuted, checks.ToImmutable());
    }

    private static bool TotalsMatch(QueryResult result, SubjectExpectation subject) =>
        Columns(result, "Total", "Count") && result.Rows.Length == 1 &&
        Number(result.Rows[0][0]) == subject.Total &&
        Number(result.Rows[0][1]) == SyntheticSubjects.Activities.Count(a => subject.ScopeKeys.Contains(a.ScopeKey));

    private static bool ScopeMatches(QueryResult result, SubjectExpectation subject)
    {
        if (!Columns(result, "ScopeKey", "Customer", "Product") || result.Rows.Length != subject.ScopeKeys.Length)
            return false;
        var found = new HashSet<int>();
        foreach (var row in result.Rows)
        {
            var key = Key(row[0]);
            if (!subject.ScopeKeys.Contains(key) || !found.Add(key) ||
                row[1] != (key <= 2 ? "A" : "B") || row[2] != (key % 2 == 1 ? "Home" : "Auto"))
                return false;
        }
        return true;
    }

    private static bool ActivityMatches(QueryResult result, SubjectExpectation subject)
    {
        if (!Columns(result, "ActivityKey", "ScopeKey", "DateKey", "Amount")) return false;
        var expected = SyntheticSubjects.Activities.Where(a => subject.ScopeKeys.Contains(a.ScopeKey))
            .ToDictionary(a => a.ActivityKey);
        if (result.Rows.Length != expected.Count) return false;
        var ids = new HashSet<int>();
        foreach (var row in result.Rows)
        {
            var key = Key(row[0]);
            if (!expected.TryGetValue(key, out var activity) || !ids.Add(key) ||
                Key(row[1]) != activity.ScopeKey || Key(row[2]) != activity.DateKey || Number(row[3]) != activity.Amount)
                return false;
        }
        return true;
    }

    private static bool AccessMatches(QueryResult result, SubjectExpectation subject) =>
        Columns(result, "SubjectKey", "ScopeKey") && result.Rows.Length == subject.ScopeKeys.Length &&
        result.Rows.All(r => r[0] == subject.SubjectKey && subject.ScopeKeys.Contains(Key(r[1]))) &&
        result.Rows.Select(r => Key(r[1])).Distinct().Count() == subject.ScopeKeys.Length;

    private static bool Columns(QueryResult result, params string[] names) =>
        result.Columns.Select(c => c.Name.Trim('[', ']')).SequenceEqual(names, StringComparer.Ordinal) &&
        result.Rows.All(r => r.Length == names.Length);

    private static int Key(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var key) ? key : -1;

    private static decimal? Number(string? value) =>
        decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var number) ? number : null;
}
