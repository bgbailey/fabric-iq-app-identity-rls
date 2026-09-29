namespace SemanticGateway;

/// <summary>How the signed-in user's key reaches the semantic model's row-level security.</summary>
public enum IdentityMode
{
    /// <summary>Send the key as <c>customData</c>; the RLS role reads it with <c>CUSTOMDATA()</c>.</summary>
    CustomData,

    /// <summary>Send the key as <c>effectiveUsername</c>; the RLS role reads it with <c>USERNAME()</c>.</summary>
    EffectiveUsername
}

/// <summary>The Power BI / Fabric side: which model to query and which app identity queries it.</summary>
public sealed class FabricSettings
{
    public string TenantId { get; set; } = "";
    public string WorkspaceId { get; set; } = "";
    public string SemanticModelId { get; set; } = "";

    /// <summary>Optional report for the Power BI Embedded comparison page.</summary>
    public string? ReportId { get; set; }

    /// <summary>The RLS role applied to every query, e.g. ExternalAppScope.</summary>
    public string RlsRole { get; set; } = "ExternalAppScope";
    public IdentityMode IdentityMode { get; set; } = IdentityMode.CustomData;

    public ServicePrincipalSettings ServicePrincipal { get; set; } = new();
}

/// <summary>The ISV's app identity. Use a certificate from the Windows store or a PEM pair.</summary>
public sealed class ServicePrincipalSettings
{
    public string ClientId { get; set; } = "";
    public string? CertificateThumbprint { get; set; }
    public string? CertificatePemPath { get; set; }
    public string? PrivateKeyPemPath { get; set; }
}

/// <summary>Fabric IQ is used for schema only, through a delegated "metadata" account.</summary>
public sealed class FabricIqSettings
{
    public string Endpoint { get; set; } = "https://fabriciq.svc.cloud.microsoft/v1/mcp/fabriciq";

    /// <summary>Public client (desktop) app registration used to sign in the metadata account.</summary>
    public string ClientId { get; set; } = "";
    public string? LoginHint { get; set; }

    /// <summary>Tables removed from the schema the model sees, e.g. the RLS mapping table.</summary>
    public string[] ExcludeTables { get; set; } = [];
    public int SchemaCacheMinutes { get; set; } = 60;
}

public sealed class AzureOpenAISettings
{
    public string Endpoint { get; set; } = "";
    public string Deployment { get; set; } = "";
}

/// <summary>
/// Who signs users in. "Development" uses the built-in demo issuer; "Oidc" trusts your
/// identity provider (Entra External ID, Auth0, Okta, ...).
/// </summary>
public sealed class AuthSettings
{
    public string Mode { get; set; } = "Development";
    public string Issuer { get; set; } = "http://localhost:5187/dev-idp";
    public string Audience { get; set; } = "api://isv-semantic-gateway";
    public string? Authority { get; set; }
    public string PublicUrl { get; set; } = "http://localhost:5187";
    public string UsersFile { get; set; } = "../../samples/users.json";

    /// <summary>Shared password for the demo users. Development mode only.</summary>
    public string DevPassword { get; set; } = "synthetic-only";
    public int DevTokenMinutes { get; set; } = 60;

    public bool IsDevelopment => string.Equals(Mode, "Development", StringComparison.OrdinalIgnoreCase);
}
