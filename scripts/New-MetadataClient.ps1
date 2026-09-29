<#
.SYNOPSIS
Creates the public client app for the one-time Fabric IQ metadata sign-in.
.DESCRIPTION
The metadata account only needs delegated permission to read and execute against
the model schema exposed by Fabric IQ MCP. End users are the ISV's own users, not
Entra users. Depending on tenant policy, an admin or the metadata user may need
to consent to these delegated Power BI scopes.
#>
param(
    [Parameter(Mandatory)] [string] $TenantId,
    [string] $DisplayName = 'ISV semantic gateway - Fabric IQ metadata'
)
$ErrorActionPreference = 'Stop'

# Graph calls through az act on the tenant you are signed in to.
if ((az account show --query tenantId -o tsv) -ne $TenantId) { throw "Sign in to the target tenant first: az login --tenant $TenantId" }

$body = @{
    displayName = $DisplayName
    signInAudience = 'AzureADMyOrg'
    isFallbackPublicClient = $true
    publicClient = @{ redirectUris = @('http://localhost') }
    requiredResourceAccess = @(@{
        resourceAppId = '00000009-0000-0000-c000-000000000000'
        resourceAccess = @(
            @{ id = 'd2bc95fc-440e-4b0e-bafd-97182de7aef5'; type = 'Scope' },
            @{ id = 'caf40b1a-f10e-4da1-86e4-5fda17eb2b07'; type = 'Scope' },
            @{ id = '7f33e027-4039-419b-938e-2f8ca153e68e'; type = 'Scope' }
        )
    })
}

$bodyPath = Join-Path ([IO.Path]::GetTempPath()) ("metadata-client-$([guid]::NewGuid()).json")
try {
    $body | ConvertTo-Json -Depth 20 | Set-Content -Encoding utf8 $bodyPath
    $app = az rest --method post --url 'https://graph.microsoft.com/v1.0/applications' --headers 'Content-Type=application/json' --body "@$bodyPath" --query '{appId:appId,id:id}' -o json | ConvertFrom-Json
} finally {
    Remove-Item $bodyPath -ErrorAction SilentlyContinue
}

[pscustomobject]@{
    ClientId = $app.appId
    ApplicationObjectId = $app.id
    AppSettings = @{
        FabricIq = @{
            ClientId = $app.appId
            LoginHint = '<metadata-account-upn>'
        }
    }
} | ConvertTo-Json -Depth 10
