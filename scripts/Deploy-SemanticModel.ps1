<#
.SYNOPSIS
Creates or updates the Fabric semantic model and refreshes it.
.DESCRIPTION
This sample keeps deployment readable: it sends every file in
model/Synthetic.SemanticModel as an inline Fabric definition part. Create returns
a model id; update reuses the id you pass. A refresh is started so the synthetic
model is ready for the gateway and report.
#>
param(
    [Parameter(Mandatory)] [string] $TenantId,
    [Parameter(Mandatory)] [string] $WorkspaceId,
    [string] $SemanticModelId,
    [string] $DisplayName = 'ISV RLS Synthetic'
)
$ErrorActionPreference = 'Stop'

function Get-Token([string] $Resource) {
    az account get-access-token --tenant $TenantId --resource $Resource --query accessToken -o tsv
}

function Invoke-Json([string] $Method, [string] $Uri, [string] $Token, $Body = $null) {
    $headers = @{ Authorization = "Bearer $Token" }
    $params = @{ Method = $Method; Uri = $Uri; Headers = $headers; ContentType = 'application/json' }
    if ($null -ne $Body) { $params.Body = ($Body | ConvertTo-Json -Depth 100) }
    Invoke-WebRequest @params
}

function Wait-FabricOperation([Microsoft.PowerShell.Commands.WebResponseObject] $Response, [string] $Token) {
    if ([int]$Response.StatusCode -ne 202) { return $Response }
    $operationId = $Response.Headers['x-ms-operation-id'] | Select-Object -First 1
    if (-not $operationId) { throw 'Fabric returned 202 without x-ms-operation-id.' }
    do {
        Start-Sleep -Seconds 5
        $stateResponse = Invoke-Json Get "https://api.fabric.microsoft.com/v1/operations/$operationId" $Token
        $state = $stateResponse.Content | ConvertFrom-Json
        if ($state.status -eq 'Failed') { throw ($state | ConvertTo-Json -Depth 20) }
    } until ($state.status -eq 'Succeeded')
    Invoke-Json Get "https://api.fabric.microsoft.com/v1/operations/$operationId/result" $Token
}

function Get-DefinitionParts([string] $Root) {
    Get-ChildItem -Path $Root -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/')
            payload = [Convert]::ToBase64String([IO.File]::ReadAllBytes($_.FullName))
            payloadType = 'InlineBase64'
        }
    }
}

$modelRoot = Resolve-Path (Join-Path $PSScriptRoot '..\model\Synthetic.SemanticModel')
$parts = @(Get-DefinitionParts $modelRoot.Path)
if ($parts.Count -eq 0) { throw "No semantic model files found under $($modelRoot.Path)." }

$fabricToken = Get-Token 'https://api.fabric.microsoft.com'
$powerBiToken = Get-Token 'https://analysis.windows.net/powerbi/api'

if ($SemanticModelId) {
    $uri = "https://api.fabric.microsoft.com/v1/workspaces/$WorkspaceId/semanticModels/$SemanticModelId/updateDefinition"
    $response = Invoke-Json Post $uri $fabricToken @{ definition = @{ parts = $parts } }
    $null = Wait-FabricOperation $response $fabricToken
    $modelId = $SemanticModelId
} else {
    $uri = "https://api.fabric.microsoft.com/v1/workspaces/$WorkspaceId/semanticModels"
    $response = Invoke-Json Post $uri $fabricToken @{ displayName = $DisplayName; definition = @{ parts = $parts } }
    $result = Wait-FabricOperation $response $fabricToken
    $model = $result.Content | ConvertFrom-Json
    $modelId = $model.id
    if (-not $modelId) { throw 'Create semantic model did not return an id.' }
}

$refreshUri = "https://api.powerbi.com/v1.0/myorg/groups/$WorkspaceId/datasets/$modelId/refreshes"
$null = Invoke-Json Post $refreshUri $powerBiToken @{ notifyOption = 'NoNotification' }
$historyUri = "https://api.powerbi.com/v1.0/myorg/groups/$WorkspaceId/datasets/$modelId/refreshes?`$top=1"
do {
    Start-Sleep -Seconds 10
    $latest = ((Invoke-Json Get $historyUri $powerBiToken).Content | ConvertFrom-Json).value | Select-Object -First 1
    if ($latest.status -in @('Failed', 'Cancelled', 'Disabled')) { throw ($latest | ConvertTo-Json -Depth 20) }
} until ($latest.status -eq 'Completed')

Write-Output $modelId
