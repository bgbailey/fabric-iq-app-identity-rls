using System.Collections.Immutable;
using System.Globalization;
using IqRls.Core;

namespace IqRls.Tests;

public sealed class VerifierTests
{
    private static QueryResult Table(string[] names, params string?[][] rows) =>
        new(names.Select(n => new ResultColumn($"[{n}]", "test-only", true)).ToImmutableArray(),
            rows.Select(r => r.ToImmutableArray()).ToImmutableArray());

    private static QueryResult Expected(ServerContext context, DaxToolInput input)
    {
        var subject = SyntheticSubjects.All.Single(s => s.SubjectKey == context.SubjectKey);
        var scopes = subject.ScopeKeys;
        var activities = SyntheticSubjects.Activities.Where(a => scopes.Contains(a.ScopeKey)).ToArray();
        if (input.Query is RlsVerifier.TotalQuery or RlsVerifier.AllQuery or RlsVerifier.RemoveFiltersQuery)
            return Table(["Total", "Count"], [subject.Total.ToString(CultureInfo.InvariantCulture), activities.Length.ToString(CultureInfo.InvariantCulture)]);
        if (input.Query == RlsVerifier.ScopeQuery)
            return Table(["ScopeKey", "Customer", "Product"], scopes.Select(k => new[]
                { k.ToString(CultureInfo.InvariantCulture), k <= 2 ? "A" : "B", k % 2 == 1 ? "Home" : "Auto" }).ToArray());
        if (input.Query == RlsVerifier.ActivityQuery)
            return Table(["ActivityKey", "ScopeKey", "DateKey", "Amount"], activities.Select(a => new[]
                { a.ActivityKey.ToString(CultureInfo.InvariantCulture), a.ScopeKey.ToString(CultureInfo.InvariantCulture),
                    a.DateKey.ToString(CultureInfo.InvariantCulture), a.Amount.ToString(CultureInfo.InvariantCulture) }).ToArray());
        if (input.Query == RlsVerifier.UserAccessQuery)
            return Table(["SubjectKey", "ScopeKey"], scopes.Select(k => new[]
                { context.SubjectKey, k.ToString(CultureInfo.InvariantCulture) }).ToArray());
        Assert.Equal(RlsVerifier.DeniedQuery(subject), input.Query);
        return Table(["DeniedCount"], ["0"]);
    }

    [Fact]
    public async Task FixedProbeSuiteCoversEverySubjectAndDoesNotClaimModelRlsForMocks()
    {
        var calls = new List<(string Subject, string Dax)>();
        var report = await RlsVerifier.VerifyAsync((context, input, _) =>
        {
            calls.Add((context.SubjectKey, input.Query));
            return Task.FromResult(Expected(context, input));
        }, liveExecuted: false);
        Assert.True(report.Passed);
        Assert.False(report.LiveExecuted);
        Assert.Equal("mock-transport-not-model-RLS", report.PrincipalMode);
        Assert.Equal(42, report.Checks.Length);
        Assert.Equal(42, calls.Count);
        Assert.Equal(6, calls.Count(c => c.Dax == RlsVerifier.TotalQuery));
        Assert.Equal(6, calls.Select(c => c.Subject).Distinct().Count());
    }

    [Theory]
    [InlineData("total")]
    [InlineData("scope")]
    [InlineData("activity")]
    [InlineData("all")]
    [InlineData("remove")]
    [InlineData("denied")]
    [InlineData("entitlements")]
    public async Task VerifierDetectsActualOverbroadResultForEachAttack(string attack)
    {
        var report = await RlsVerifier.VerifyAsync((context, input, _) =>
        {
            if (context.SubjectKey == "app-user-A1")
            {
                if ((attack == "total" && input.Query == RlsVerifier.TotalQuery) ||
                    (attack == "all" && input.Query == RlsVerifier.AllQuery) ||
                    (attack == "remove" && input.Query == RlsVerifier.RemoveFiltersQuery))
                    return Task.FromResult(Table(["Total", "Count"], ["1950", "4"]));
                if (attack == "scope" && input.Query == RlsVerifier.ScopeQuery)
                    return Task.FromResult(Table(["ScopeKey", "Customer", "Product"], ["3", "B", "Home"]));
                if (attack == "activity" && input.Query == RlsVerifier.ActivityQuery)
                    return Task.FromResult(Table(["ActivityKey", "ScopeKey", "DateKey", "Amount"], ["3", "3", "20260928", "250"]));
                if (attack == "entitlements" && input.Query == RlsVerifier.UserAccessQuery)
                    return Task.FromResult(Table(["SubjectKey", "ScopeKey"], ["app-user-A2", "1"]));
                if (attack == "denied" && input.Query == RlsVerifier.DeniedQuery(SyntheticSubjects.All[0]))
                    return Task.FromResult(Table(["DeniedCount"], ["1"]));
            }
            return Task.FromResult(Expected(context, input));
        }, liveExecuted: false);
        Assert.False(report.Passed);
        Assert.Contains(report.Checks, c => c.Subject == "app-user-A1" && !c.Passed);
        Assert.All(report.Checks.Where(c => c.Subject != "app-user-A1"), c => Assert.True(c.Passed));
    }

    [Fact]
    public async Task NoAccessSubjectCannotReceiveRowsEvenIfTotalsAreZero()
    {
        var report = await RlsVerifier.VerifyAsync((context, input, _) =>
            Task.FromResult(context.SubjectKey == "app-user-none" && input.Query == RlsVerifier.ActivityQuery
                ? Table(["ActivityKey", "ScopeKey", "DateKey", "Amount"], ["1", "1", "20260928", "0"])
                : Expected(context, input)), liveExecuted: false);
        Assert.False(report.Passed);
        Assert.Contains(report.Checks, c => c.Subject == "app-user-none" && c.Check == "direct-activity-projection" && !c.Passed);
    }
}
