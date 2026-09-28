using System.Text.Json;

namespace IqRls.Core;

public sealed class LiveConfiguration
{
    private LiveConfiguration(Guid tenantId, Guid clientId, Guid workspaceId, Guid datasetId, string thumbprint)
    {
        TenantId = tenantId;
        ClientId = clientId;
        WorkspaceId = workspaceId;
        DatasetId = datasetId;
        CertificateThumbprint = thumbprint;
    }

    public Guid TenantId { get; }
    public Guid ClientId { get; }
    public Guid WorkspaceId { get; }
    public Guid DatasetId { get; }
    public string CertificateThumbprint { get; }
    public string AuthMode => "certificate";
    public string Role => HarnessContract.Role;
    public string ModelAlias => HarnessContract.ModelAlias;
    public string EntitlementVersion => HarnessContract.EntitlementVersion;
    public Uri Endpoint => new(
        $"https://api.powerbi.com/v1.0/myorg/groups/{WorkspaceId:D}/datasets/{DatasetId:D}/executeDaxQueries");

    public const string Template = """
        {
          "authMode": "certificate",
          "tenantId": "<tenant-guid>",
          "clientId": "<application-client-guid>",
          "workspaceId": "<isolated-synthetic-workspace-guid>",
          "datasetId": "<synthetic-model-guid>",
          "certificateThumbprint": "<40-hex-thumbprint-in-CurrentUser-My>",
          "modelAlias": "synthetic-rls-v1",
          "entitlementVersion": "synthetic-v1",
          "role": "ExternalAppScope"
        }
        """;

    public static LiveConfiguration Parse(string json)
    {
        if (json.Length > 16384) throw new HarnessException(FailureCode.InvalidConfiguration);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            string[] names = ["authMode", "tenantId", "clientId", "workspaceId", "datasetId",
                "certificateThumbprint", "modelAlias", "entitlementVersion", "role"];
            if (root.ValueKind != JsonValueKind.Object)
                throw new HarnessException(FailureCode.InvalidConfiguration);
            var supplied = root.EnumerateObject().Select(p => p.Name).ToArray();
            if (supplied.Length != names.Length || supplied.Distinct(StringComparer.Ordinal).Count() != names.Length ||
                supplied.Any(n => !names.Contains(n, StringComparer.Ordinal)))
                throw new HarnessException(FailureCode.InvalidConfiguration);
            string Required(string name) =>
                root.GetProperty(name).ValueKind == JsonValueKind.String
                    ? root.GetProperty(name).GetString()!
                    : throw new HarnessException(FailureCode.InvalidConfiguration);
            Guid Id(string name) => Guid.TryParseExact(Required(name), "D", out var id) && id != Guid.Empty
                ? id : throw new HarnessException(FailureCode.InvalidConfiguration);
            if (Required("authMode") != "certificate" || Required("modelAlias") != HarnessContract.ModelAlias ||
                Required("entitlementVersion") != HarnessContract.EntitlementVersion || Required("role") != HarnessContract.Role)
                throw new HarnessException(FailureCode.InvalidConfiguration);
            var thumbprint = Required("certificateThumbprint");
            if (thumbprint.Length != 40 || !thumbprint.All(Uri.IsHexDigit))
                throw new HarnessException(FailureCode.InvalidConfiguration);
            return new(Id("tenantId"), Id("clientId"), Id("workspaceId"), Id("datasetId"), thumbprint.ToUpperInvariant());
        }
        catch (JsonException)
        {
            throw new HarnessException(FailureCode.InvalidConfiguration);
        }
    }
}
