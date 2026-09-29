using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Azure;
using Azure.Core;
using Azure.Identity;

namespace IqRls.Core;

public interface IAccessTokenSource
{
    ValueTask<string> GetTokenAsync(CancellationToken cancellationToken);
}

public sealed class CertificateTokenSource : IAccessTokenSource, IDisposable
{
    private readonly X509Certificate2 certificate;
    private readonly ClientCertificateCredential credential;

    public CertificateTokenSource(LiveConfiguration configuration)
    {
        certificate = configuration.CertificateThumbprint is not null
            ? LoadFromWindowsStore(configuration.CertificateThumbprint)
            : LoadFromPem(configuration.CertificatePemPath!, configuration.PrivateKeyPemPath!);
        credential = new ClientCertificateCredential(configuration.TenantId.ToString("D"),
            configuration.ClientId.ToString("D"), certificate, new ClientCertificateCredentialOptions
            {
                AuthorityHost = AzureAuthorityHosts.AzurePublicCloud,
                SendCertificateChain = false,
                DisableInstanceDiscovery = true
            });
    }

    private static X509Certificate2 LoadFromWindowsStore(string thumbprint)
    {
        if (!OperatingSystem.IsWindows()) throw new HarnessException(FailureCode.CertificateUnavailable);
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
            if (matches.Count != 1) throw new HarnessException(FailureCode.CertificateUnavailable);
            var certificate = matches[0];
            try
            {
                var now = DateTime.UtcNow;
                if (!certificate.HasPrivateKey || now < certificate.NotBefore.ToUniversalTime() ||
                    now >= certificate.NotAfter.ToUniversalTime())
                    throw new HarnessException(FailureCode.CertificateUnavailable);
                using var rsa = certificate.GetRSAPrivateKey();
                var nonExportable = rsa switch
                {
                    RSACng cng => cng.Key.ExportPolicy == CngExportPolicies.None,
                    RSACryptoServiceProvider csp => !csp.CspKeyContainerInfo.Exportable,
                    _ => false
                };
                if (!nonExportable) throw new HarnessException(FailureCode.CertificateUnavailable);
                return certificate;
            }
            catch
            {
                certificate.Dispose();
                throw;
            }
        }
        catch (Exception error) when (error is CryptographicException or UnauthorizedAccessException)
        {
            throw new HarnessException(FailureCode.CertificateUnavailable);
        }
    }

    private static X509Certificate2 LoadFromPem(string certificatePath, string privateKeyPath)
    {
        try
        {
            var certificate = X509Certificate2.CreateFromPemFile(certificatePath, privateKeyPath);
            var now = DateTime.UtcNow;
            if (!certificate.HasPrivateKey || now < certificate.NotBefore.ToUniversalTime() ||
                now >= certificate.NotAfter.ToUniversalTime())
                throw new HarnessException(FailureCode.CertificateUnavailable);
            return certificate;
        }
        catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(FailureCode.CertificateUnavailable);
        }
    }

    public async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            var token = await credential.GetTokenAsync(
                new TokenRequestContext([HarnessContract.ResourceScope]), cancellationToken).ConfigureAwait(false);
            return token.Token;
        }
        catch (Exception error) when (error is AuthenticationFailedException or RequestFailedException)
        {
            throw new HarnessException(FailureCode.AuthenticationFailed);
        }
    }

    public void Dispose() => certificate.Dispose();
}
