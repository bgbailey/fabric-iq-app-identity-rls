<#
.SYNOPSIS
Adds a user, group, or service principal to a Fabric workspace.
.DESCRIPTION
For the gateway service principal, use Admin. Power BI executeDaxQueries only
lets a caller choose an RLS role through the roles property when that caller is a
workspace Admin. RLS still applies because the gateway always sends the fixed
role and the user's opaque key as customData.
#>
param(
    [Parameter(Mandatory)] [string] $TenantId,
    [Parameter(Mandatory)] [string] $WorkspaceId,
    [Parameter(Mandatory)] [string] $PrincipalId,
    [ValidateSet('ServicePrincipal', 'User', 'Group')] [string] $PrincipalType = 'ServicePrincipal',
    [ValidateSet('Admin', 'Member', 'Contributor', 'Viewer')] [string] $Role = 'Admin'
)
$ErrorActionPreference = 'Stop'

$token = az account get-access-token --tenant $TenantId --resource https://api.fabric.microsoft.com --query accessToken -o tsv
$body = @{
    principal = @{ id = $PrincipalId; type = $PrincipalType }
    role = $Role
} | ConvertTo-Json -Depth 5

Invoke-WebRequest `
    -Method Post `
    -Uri "https://api.fabric.microsoft.com/v1/workspaces/$WorkspaceId/roleAssignments" `
    -Headers @{ Authorization = "Bearer $token" } `
    -ContentType 'application/json' `
    -Body $body | Out-Null

Write-Host "Added $PrincipalType $PrincipalId as $Role."
