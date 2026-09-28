using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ModelDefinition;

internal sealed record PackageResult(byte[] Body, byte[] Manifest, string ApprovalHash);

internal static class DefinitionPackage
{
    internal const string UpstreamCommit = "6c11ad58c25992e5d1435ce7cd80d217d5598a31";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly UTF8Encoding Utf8 = new(false);

    internal static PackageResult Build(ValidatedDefinition model)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            displayName = "IQ RLS Synthetic",
            description = "Synthetic inline Import model for an app-identity RLS experiment; live validation still required.",
            definition = new
            {
                format = "TMDL",
                parts = model.Parts.Select(part => new
                {
                    path = part.Key, payload = Convert.ToBase64String(part.Value), payloadType = "InlineBase64"
                }).ToArray()
            }
        }, JsonOptions);
        var hashes = model.Parts.Select(part => new
        {
            path = part.Key, bytes = part.Value.Length, sha256 = Hash(part.Value)
        }).ToArray();
        var fixtureHash = Hash(model.FixtureBytes);
        var bodyHash = Hash(body);
        // Hash raw bytes, ordinal part paths, LF delimiters, and fixture expectations; no timestamp or machine paths.
        var approvalInput = "IQ-RLS-APPROVAL-v1\n" + UpstreamCommit + "\n"
            + string.Join("", hashes.Select(part => FormattableString.Invariant($"{part.path}\t{part.bytes}\t{part.sha256}\n")))
            + FormattableString.Invariant($"fixtures.json\t{model.FixtureBytes.Length}\t{fixtureHash}\n")
            + FormattableString.Invariant($"createSemanticModel.json\t{body.Length}\t{bodyHash}\n");
        var approvalHash = Hash(Utf8.GetBytes(approvalInput));
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            displayName = "IQ RLS Synthetic",
            approvalSha256 = approvalHash,
            upstreamCommit = UpstreamCommit,
            tomPackage = "Microsoft.AnalysisServices",
            tomPackageVersion = "19.114.12",
            validation = new
            {
                tmdlSyntax = "TOM TmdlSerializer deserialize/serialize/deserialize",
                exactFixtureAndPolicyChecked = true,
                daxEngineExecuted = false,
                powerQueryEngineExecuted = false,
                liveRlsValidated = false,
                deploymentApproved = false
            },
            definitionParts = hashes,
            fixtures = new { path = "model/fixtures.json", bytes = model.FixtureBytes.Length, sha256 = fixtureHash },
            request = new { path = "createSemanticModel.json", bytes = body.Length, sha256 = bodyHash },
            digestRecipe = "SHA256 UTF8: IQ-RLS-APPROVAL-v1 LF, upstreamCommit LF, each ordinal part path TAB byteLength TAB sha256 LF, fixtures.json TAB byteLength TAB sha256 LF, createSemanticModel.json TAB byteLength TAB sha256 LF. Decimal byte lengths are invariant-culture."
        }, JsonOptions);
        return new(body, manifest, approvalHash);
    }

    internal static string Publish(string root, PackageResult package)
    {
        var destination = Path.Combine(root, "artifacts", "model-definition", "packages", package.ApprovalHash);
        Directory.CreateDirectory(destination);
        WriteOnce(Path.Combine(destination, "createSemanticModel.json"), package.Body);
        WriteOnce(Path.Combine(destination, "approval.manifest.json"), package.Manifest);
        return destination;
    }

    private static void WriteOnce(string path, byte[] bytes)
    {
        if (!File.Exists(path))
        {
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(bytes);
        }
        Check.That(File.ReadAllBytes(path).SequenceEqual(bytes), "Refusing to overwrite a differing artifact: " + path);
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
