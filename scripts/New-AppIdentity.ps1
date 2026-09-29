<#
.SYNOPSIS
Creates the confidential app identity used by the ISV gateway.
.DESCRIPTION
The gateway uses this service principal with a local non-exportable certificate
to call executeDaxQueries and create embedded tokens. For production, prefer Key
Vault certificate storage or a managed identity. A tenant admin must also allow
service principals to use Fabric and Power BI APIs.
#>
param(
    [Parameter(Mandatory)] [string] $TenantId,
    [string] $DisplayName = 'ISV semantic gateway',
    [int] $CertificateMonths = 6
)
$ErrorActionPreference = 'Stop'

# az ad commands act on the tenant you are signed in to.
if ((az account show --query tenantId -o tsv) -ne $TenantId) { throw "Sign in to the target tenant first: az login --tenant $TenantId" }

$app = az ad app create --display-name $DisplayName --sign-in-audience AzureADMyOrg --query '{appId:appId,id:id}' -o json | ConvertFrom-Json
$sp = az ad sp create --id $app.appId --query '{id:id}' -o json | ConvertFrom-Json

$cert = New-SelfSignedCertificate `
    -Subject "CN=$DisplayName" `
    -CertStoreLocation Cert:\CurrentUser\My `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -KeyExportPolicy NonExportable `
    -NotAfter (Get-Date).AddMonths($CertificateMonths)

$cerPath = Join-Path ([IO.Path]::GetTempPath()) ("$($app.appId).cer")
try {
    Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null
    az ad app credential reset --id $app.appId --cert "@$cerPath" --append --only-show-errors | Out-Null
} finally {
    Remove-Item $cerPath -ErrorAction SilentlyContinue
}

[pscustomobject]@{
    ClientId = $app.appId
    ServicePrincipalObjectId = $sp.id
    CertificateThumbprint = $cert.Thumbprint
    AppSettings = @{
        Fabric = @{
            TenantId = $TenantId
            ServicePrincipal = @{
                ClientId = $app.appId
                CertificateThumbprint = $cert.Thumbprint
            }
        }
    }
} | ConvertTo-Json -Depth 10
