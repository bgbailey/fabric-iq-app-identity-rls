using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ModelDefinition;

internal static class OfflineTests
{
    internal static int Run(string root, ValidatedDefinition model)
    {
        var count = 0;
        void Pass(string name, Action test)
        {
            test();
            Console.WriteLine("PASS " + name);
            count++;
        }
        void Rejected(string name, Action test, string? exceptionType = null) => Pass(name, () =>
        {
            Exception? failure = null;
            try { test(); }
            catch (Exception error) { failure = error; }
            Check.That(failure is not null, name + ": unexpectedly accepted");
            if (exceptionType is not null)
                Check.That(failure!.GetType().Name == exceptionType, name + ": wrong rejection type " + failure.GetType().Name);
        });
        void Mutation(string part, string before, string after)
        {
            var parts = new SortedDictionary<string, byte[]>(model.Parts, StringComparer.Ordinal);
            var text = Encoding.UTF8.GetString(parts[part]);
            Check.That(text.Contains(before, StringComparison.Ordinal), "Test mutation did not match");
            parts[part] = Encoding.UTF8.GetBytes(text.Replace(before, after, StringComparison.Ordinal));
            ModelValidator.ValidateParts(root, parts, model.Fixture);
        }

        Pass("TOM parses and round-trips every table, relationship, role and expression",
            () => ModelValidator.ValidateParts(root, model.Parts, model.Fixture));
        Pass("Fixture arithmetic, exact pairs, blank/unknown subjects and neutral calendar", model.Fixture.Validate);
        foreach (var part in ModelValidator.RequiredParts)
        {
            Rejected("Missing part rejected: " + part, () =>
            {
                var parts = new SortedDictionary<string, byte[]>(model.Parts, StringComparer.Ordinal);
                parts.Remove(part);
                ModelValidator.ValidateParts(root, parts, model.Fixture);
            }, nameof(InvalidDataException));
        }
        Rejected("Unknown TMDL property rejected by genuine TOM parser", () =>
            Mutation("definition/database.tmdl", "compatibilityLevel: 1702", "notARealTmdlProperty: 1702"), "TmdlFormatException");
        Rejected("Removed CUSTOMDATA role reference rejected", () =>
            Mutation("definition/model.tmdl", "ref role ExternalAppScope", ""), nameof(InvalidDataException));
        Rejected("Removed USERNAME role reference rejected", () =>
            Mutation("definition/model.tmdl", "ref role ExternalAppUsername", ""), nameof(InvalidDataException));
        Rejected("Open scope policy rejected", () =>
            Mutation("definition/roles/ExternalAppScope.tmdl", "NOT ISBLANK(_subject)", "TRUE()"), nameof(InvalidDataException));
        Rejected("Entitlement enumeration policy rejected", () =>
            Mutation("definition/roles/ExternalAppScope.tmdl", "EXACT('User Access'[Subject Key], _subject)", "TRUE()"),
            nameof(InvalidDataException));
        Rejected("Open USERNAME scope policy rejected", () =>
            Mutation("definition/roles/ExternalAppUsername.tmdl", "NOT ISBLANK(_subject)", "TRUE()"), nameof(InvalidDataException));
        Rejected("USERNAME entitlement enumeration policy rejected", () =>
            Mutation("definition/roles/ExternalAppUsername.tmdl", "EXACT('User Access'[Subject Key], _subject)", "TRUE()"),
            nameof(InvalidDataException));
        Rejected("Bidirectional propagation rejected", () =>
            Mutation("definition/relationships.tmdl", "crossFilteringBehavior: oneDirection", "crossFilteringBehavior: bothDirections"),
            nameof(InvalidDataException));
        Rejected("Inactive security relationship rejected", () =>
            Mutation("definition/relationships.tmdl", "isActive: true", "isActive: false"), nameof(InvalidDataException));
        Rejected("Bidirectional security propagation rejected", () =>
            Mutation("definition/relationships.tmdl", "securityFilteringBehavior: oneDirection", "securityFilteringBehavior: bothDirections"),
            nameof(InvalidDataException));
        Rejected("Open MetadataOnly role rejected", () =>
            Mutation("definition/roles/MetadataOnly.tmdl", "FALSE()", "TRUE()"), nameof(InvalidDataException));
        Rejected("Elevated role permission rejected", () =>
            Mutation("definition/roles/ExternalAppUsername.tmdl", "modelPermission: read", "modelPermission: administrator"),
            nameof(InvalidDataException));
        Rejected("External M connector rejected", () =>
            Mutation("definition/tables/Scope.tmdl", "#table(", "Web.Contents("), nameof(InvalidDataException));
        Rejected("Changed source fixture rejected", () =>
            Mutation("definition/tables/Activity.tmdl", "{1, 1, 20260101, 100}", "{1, 1, 20260101, 999}"), nameof(InvalidDataException));
        Rejected("Floating-point amount rejected", () =>
            Mutation("definition/tables/Activity.tmdl", "dataType: decimal", "dataType: double"), nameof(InvalidDataException));
        Rejected("Additional platform/definition file rejected", () =>
        {
            var parts = new SortedDictionary<string, byte[]>(model.Parts, StringComparer.Ordinal) { [".platform"] = "{}"u8.ToArray() };
            ModelValidator.ValidateParts(root, parts, model.Fixture);
        }, nameof(InvalidDataException));
        Rejected("Altered expected total rejected", () =>
        {
            var cases = model.Fixture.Cases.ToArray();
            cases[0] = cases[0] with { TotalAmount = 999 };
            (model.Fixture with { Cases = cases }).Validate();
        }, nameof(InvalidDataException));
        Rejected("Cartesian-expanded pair grants rejected", () =>
        {
            var grants = model.Fixture.UserAccess.Append(new AccessRow("app-user-pairs", 2)).ToArray();
            (model.Fixture with { UserAccess = grants }).Validate();
        }, nameof(InvalidDataException));

        var package = DefinitionPackage.Build(model);
        Pass("Complete base64 request round-trips every source byte; no .platform", () =>
        {
            using var json = JsonDocument.Parse(package.CreateBody);
            var parts = json.RootElement.GetProperty("definition").GetProperty("parts").EnumerateArray().ToArray();
            Check.Sequence(parts.Select(part => part.GetProperty("path").GetString()), ModelValidator.RequiredParts, "Packaged part set");
            foreach (var part in parts)
            {
                var path = part.GetProperty("path").GetString()!;
                Check.That(part.GetProperty("payloadType").GetString() == "InlineBase64", "Payload type");
                Check.Sequence(Convert.FromBase64String(part.GetProperty("payload").GetString()!), model.Parts[path], "Payload bytes " + path);
            }
        });
        Pass("Update-definition request round-trips every source byte and omits .platform", () =>
        {
            using var json = JsonDocument.Parse(package.UpdateBody);
            var parts = json.RootElement.GetProperty("definition").GetProperty("parts").EnumerateArray().ToArray();
            Check.That(json.RootElement.GetProperty("definition").GetProperty("format").GetString() == "TMDL", "Update format");
            Check.Sequence(parts.Select(part => part.GetProperty("path").GetString()), ModelValidator.RequiredParts, "Update packaged part set");
            Check.That(!parts.Any(part => part.GetProperty("path").GetString() == ".platform"), "Update body must not include .platform");
            foreach (var part in parts)
            {
                var path = part.GetProperty("path").GetString()!;
                Check.That(part.GetProperty("payloadType").GetString() == "InlineBase64", "Update payload type");
                Check.Sequence(Convert.FromBase64String(part.GetProperty("payload").GetString()!), model.Parts[path], "Update payload bytes " + path);
            }
        });
        Pass("Package and approval manifest deterministic across runs/cultures", () =>
        {
            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                var again = DefinitionPackage.Build(model);
                Check.Sequence(again.CreateBody, package.CreateBody, "Deterministic create request");
                Check.Sequence(again.UpdateBody, package.UpdateBody, "Deterministic update request");
                Check.Sequence(again.Manifest, package.Manifest, "Deterministic manifest");
                Check.That(again.ApprovalHash == package.ApprovalHash, "Deterministic approval hash");
            }
            finally { CultureInfo.CurrentCulture = culture; }
        });
        Pass("Any changed definition or fixture byte changes approval hash", () =>
        {
            var parts = new SortedDictionary<string, byte[]>(model.Parts, StringComparer.Ordinal);
            parts["definition.pbism"] = [.. parts["definition.pbism"], (byte)'\n'];
            Check.That(DefinitionPackage.Build(model with { Parts = parts }).ApprovalHash != package.ApprovalHash, "Part tamper binding");
            Check.That(DefinitionPackage.Build(model with { FixtureBytes = [.. model.FixtureBytes, (byte)'\n'] }).ApprovalHash
                != package.ApprovalHash, "Fixture tamper binding");
        });
        Pass("Manifest request and all part hashes match actual bytes", () =>
        {
            using var manifest = JsonDocument.Parse(package.Manifest);
            var requests = manifest.RootElement.GetProperty("requests").EnumerateArray().ToDictionary(e => e.GetProperty("path").GetString()!);
            Check.That(requests["createSemanticModel.json"].GetProperty("sha256").GetString() == DefinitionPackage.Hash(package.CreateBody),
                "Create request hash");
            Check.That(requests["updateSemanticModelDefinition.json"].GetProperty("sha256").GetString() == DefinitionPackage.Hash(package.UpdateBody),
                "Update request hash");
            foreach (var part in manifest.RootElement.GetProperty("definitionParts").EnumerateArray())
                Check.That(part.GetProperty("sha256").GetString() == DefinitionPackage.Hash(model.Parts[part.GetProperty("path").GetString()!]),
                    "Part hash");
        });
        return count;
    }
}
