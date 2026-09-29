using System.Text.Json;

namespace IqRls.Core;

public enum QueryIdentityMode { CustomData, EffectiveUsername }

public sealed class LiveConfiguration
{
    private LiveConfiguration(Guid tenantId, Guid clientId, Guid workspaceId, Guid datasetId,
        string? certificateThumbprint, string? certificatePemPath, string? privateKeyPemPath,
        QueryIdentityMode identityMode)
    {
        TenantId = tenantId;
        ClientId = clientId;
        WorkspaceId = workspaceId;
        DatasetId = datasetId;
        CertificateThumbprint = certificateThumbprint;
        CertificatePemPath = certificatePemPath;
        PrivateKeyPemPath = privateKeyPemPath;
        IdentityMode = identityMode;
    }

    public Guid TenantId { get; }
    public Guid ClientId { get; }
    public Guid WorkspaceId { get; }
    public Guid DatasetId { get; }
    public string? CertificateThumbprint { get; }
    public string? CertificatePemPath { get; }
    public string? PrivateKeyPemPath { get; }
    public QueryIdentityMode IdentityMode { get; }
    public string AuthMode => "certificate";
    public string Role => IdentityMode == QueryIdentityMode.CustomData ? HarnessContract.Role : HarnessContract.UsernameRole;
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
          "identityMode": "customData",
          "modelAlias": "synthetic-rls-v1",
          "entitlementVersion": "synthetic-v1",
          "role": "ExternalAppScope"
        }
        """;

    public LiveConfiguration WithIdentityMode(QueryIdentityMode identityMode) =>
        new(TenantId, ClientId, WorkspaceId, DatasetId, CertificateThumbprint, CertificatePemPath,
            PrivateKeyPemPath, identityMode);

    public static LiveConfiguration Parse(string json)
    {
        if (json.Length > 16384) throw new HarnessException(FailureCode.InvalidConfiguration);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new HarnessException(FailureCode.InvalidConfiguration);
            var allowed = new[] { "authMode", "tenantId", "clientId", "workspaceId", "datasetId",
                "certificateThumbprint", "certificatePemPath", "privateKeyPemPath", "identityMode",
                "modelAlias", "entitlementVersion", "role" };
            var supplied = root.EnumerateObject().Select(p => p.Name).ToArray();
            if (supplied.Distinct(StringComparer.Ordinal).Count() != supplied.Length ||
                supplied.Any(n => !allowed.Contains(n, StringComparer.Ordinal)))
                throw new HarnessException(FailureCode.InvalidConfiguration);
            string Required(string name) =>
                root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()!
                    : throw new HarnessException(FailureCode.InvalidConfiguration);
            string? Optional(string name) =>
                root.TryGetProperty(name, out var value)
                    ? value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new HarnessException(FailureCode.InvalidConfiguration)
                    : null;
            Guid Id(string name) => Guid.TryParseExact(Required(name), "D", out var id) && id != Guid.Empty
                ? id : throw new HarnessException(FailureCode.InvalidConfiguration);
            if (Required("authMode") != "certificate" || Required("modelAlias") != HarnessContract.ModelAlias ||
                Required("entitlementVersion") != HarnessContract.EntitlementVersion)
                throw new HarnessException(FailureCode.InvalidConfiguration);
            var identityMode = Optional("identityMode") switch
            {
                null or "customData" => QueryIdentityMode.CustomData,
                "effectiveUsername" => QueryIdentityMode.EffectiveUsername,
                _ => throw new HarnessException(FailureCode.InvalidConfiguration)
            };
            var expectedRole = identityMode == QueryIdentityMode.CustomData ? HarnessContract.Role : HarnessContract.UsernameRole;
            if (Required("role") != expectedRole) throw new HarnessException(FailureCode.InvalidConfiguration);
            var thumbprint = Optional("certificateThumbprint");
            var certPem = Optional("certificatePemPath");
            var keyPem = Optional("privateKeyPemPath");
            var hasThumbprint = thumbprint is not null;
            var hasPemPair = certPem is not null || keyPem is not null;
            if (hasThumbprint == hasPemPair || (hasPemPair && (certPem is null || keyPem is null)))
                throw new HarnessException(FailureCode.InvalidConfiguration);
            if (thumbprint is not null)
            {
                if (thumbprint.Length != 40 || !thumbprint.All(Uri.IsHexDigit))
                    throw new HarnessException(FailureCode.InvalidConfiguration);
                thumbprint = thumbprint.ToUpperInvariant();
            }
            if (certPem is not null && (!Path.IsPathFullyQualified(certPem) || !Path.IsPathFullyQualified(keyPem!)))
                throw new HarnessException(FailureCode.InvalidConfiguration);
            return new(Id("tenantId"), Id("clientId"), Id("workspaceId"), Id("datasetId"),
                thumbprint, certPem, keyPem, identityMode);
        }
        catch (JsonException)
        {
            throw new HarnessException(FailureCode.InvalidConfiguration);
        }
    }
}
