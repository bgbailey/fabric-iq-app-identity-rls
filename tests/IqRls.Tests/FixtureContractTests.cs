using System.Text.Json;
using IqRls.Core;

namespace IqRls.Tests;

public sealed class FixtureContractTests
{
    [Fact]
    public void BrokerAndVerifierConstantsMatchTheAuthoredModelFixture()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "model-fixtures.json")));
        var root = document.RootElement;
        Assert.Equal(HarnessContract.ModelAlias, root.GetProperty("modelAlias").GetString());
        Assert.Equal(HarnessContract.EntitlementVersion, root.GetProperty("entitlementVersion").GetString());
        Assert.Equal("synthetic-only", root.GetProperty("classification").GetString());
        Assert.False(root.GetProperty("liveValidated").GetBoolean());
        Assert.Equal(HarnessContract.Role, root.GetProperty("queryExpectations").GetProperty("scopeRole").GetString());
        var activities = root.GetProperty("activities").EnumerateArray().Select(a => new ActivityExpectation(
            a.GetProperty("activityKey").GetInt32(), a.GetProperty("scopeKey").GetInt32(),
            a.GetProperty("dateKey").GetInt32(), a.GetProperty("amount").GetDecimal())).ToArray();
        Assert.Equal(activities, SyntheticSubjects.Activities.ToArray());
        Assert.Equal(1950m, activities.Sum(a => a.Amount));
        var cases = root.GetProperty("cases").EnumerateArray().ToArray();
        foreach (var subject in SyntheticSubjects.All)
        {
            var fixture = Assert.Single(cases.Where(c =>
                c.GetProperty("customData").GetString() == subject.SubjectKey &&
                (!c.TryGetProperty("role", out var role) || role.GetString() == HarnessContract.Role)));
            var allowed = activities.Where(a => subject.ScopeKeys.Contains(a.ScopeKey)).ToArray();
            Assert.Equal(subject.ScopeKeys.ToArray(), fixture.GetProperty("scopeKeys").EnumerateArray().Select(v => v.GetInt32()).ToArray());
            Assert.Equal(allowed.Select(a => a.ActivityKey).ToArray(),
                fixture.GetProperty("activityKeys").EnumerateArray().Select(v => v.GetInt32()).ToArray());
            Assert.Equal(allowed.Length, fixture.GetProperty("rowCount").GetInt32());
            var total = fixture.GetProperty("totalAmount");
            Assert.Equal(subject.Total, total.ValueKind == JsonValueKind.Null ? 0m : total.GetDecimal());
            Assert.Equal(subject.Total, allowed.Sum(a => a.Amount));
            var entitlements = root.GetProperty("userAccess").EnumerateArray()
                .Where(a => a.GetProperty("subjectKey").GetString() == subject.SubjectKey)
                .Select(a => a.GetProperty("scopeKey").GetInt32()).ToArray();
            Assert.Equal(subject.ScopeKeys.ToArray(), entitlements);
        }
    }
}
