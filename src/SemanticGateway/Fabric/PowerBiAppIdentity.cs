using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;

namespace SemanticGateway.Fabric;

/// <summary>
/// The ISV's app identity: an Entra service principal that authenticates with a certificate.
/// It is the only identity that reads data, for every end user, exactly like "app owns data"
/// in Power BI Embedded. It must be able to query the model (workspace Admin in this sample,
/// because only workspace admins may pick an RLS role on executeDaxQueries).
/// </summary>
public sealed class PowerBiAppIdentity
{
    private static readonly string[] PowerBiScope = ["https://analysis.windows.net/powerbi/api/.default"];
    private readonly ClientCertificateCredential credential;

    public PowerBiAppIdentity(IOptions<FabricSettings> options)
    {
        var fabric = options.Value;
        credential = new ClientCertificateCredential(fabric.TenantId, fabric.ServicePrincipal.ClientId, LoadCertificate(fabric.ServicePrincipal));
    }

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken) =>
        (await credential.GetTokenAsync(new TokenRequestContext(PowerBiScope), cancellationToken)).Token;

    private static X509Certificate2 LoadCertificate(ServicePrincipalSettings sp)
    {
        if (sp.CertificatePemPath is not null && sp.PrivateKeyPemPath is not null)
            return X509Certificate2.CreateFromPemFile(sp.CertificatePemPath, sp.PrivateKeyPemPath);

        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, sp.CertificateThumbprint ?? "", validOnly: false);
        return matches.Count == 1
            ? matches[0]
            : throw new InvalidOperationException($"Certificate {sp.CertificateThumbprint} was not found in CurrentUser\\My.");
    }
}
