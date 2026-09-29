<#
.SYNOPSIS
Creates the optional PBIR comparison report.
.DESCRIPTION
The report is not required for the gateway pattern, but it is useful for showing
that Power BI Embedded and the DAX query path use the same semantic model. The
script packages model/Synthetic.Report and replaces the semantic model id
placeholder in definition.pbir before creating the Fabric report.
#>
param(
    [Parameter(Mandatory)] [string] $TenantId,
    [Parameter(Mandatory)] [string] $WorkspaceId,
    [Parameter(Mandatory)] [string] $SemanticModelId,
    [string] $DisplayName = 'Authorized activity'
)
$ErrorActionPreference = 'Stop'

function Get-Token([string] $Resource) {
    az account get-access-token --tenant $TenantId --resource $Resource --query accessToken -o tsv
}

function Invoke-Json([string] $Method, [string] $Uri, [string] $Token, $Body = $null) {
    $params = @{ Method = $Method; Uri = $Uri; Headers = @{ Authorization = "Bearer $Token" }; ContentType = 'application/json' }
    if ($null -ne $Body) { $params.Body = ($Body | ConvertTo-Json -Depth 100) }
    Invoke-WebRequest @params
}

function Wait-FabricOperation([Microsoft.PowerShell.Commands.WebResponseObject] $Response, [string] $Token) {
    if ([int]$Response.StatusCode -ne 202) { return $Response }
    $operationId = $Response.Headers['x-ms-operation-id'] | Select-Object -First 1
    if (-not $operationId) { throw 'Fabric returned 202 without x-ms-operation-id.' }
    do {
        Start-Sleep -Seconds 5
        $state = ((Invoke-Json Get "https://api.fabric.microsoft.com/v1/operations/$operationId" $Token).Content | ConvertFrom-Json)
        if ($state.status -eq 'Failed') { throw ($state | ConvertTo-Json -Depth 20) }
    } until ($state.status -eq 'Succeeded')
    Invoke-Json Get "https://api.fabric.microsoft.com/v1/operations/$operationId/result" $Token
}

$reportRoot = Resolve-Path (Join-Path $PSScriptRoot '..\model\Synthetic.Report')
$parts = @(Get-ChildItem -Path $reportRoot.Path -File -Recurse | Sort-Object FullName | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($reportRoot.Path, $_.FullName).Replace('\', '/')
    $bytes = if ($relative -eq 'definition.pbir') {
        [Text.Encoding]::UTF8.GetBytes((Get-Content $_.FullName -Raw).Replace('{{SEMANTIC_MODEL_ID}}', $SemanticModelId))
    } else {
        [IO.File]::ReadAllBytes($_.FullName)
    }
    [ordered]@{ path = $relative; payload = [Convert]::ToBase64String($bytes); payloadType = 'InlineBase64' }
})
if ($parts.Count -eq 0) { throw "No report files found under $($reportRoot.Path)." }

$token = Get-Token 'https://api.fabric.microsoft.com'
$body = @{ displayName = $DisplayName; definition = @{ format = 'PBIR'; parts = $parts } }
$response = Invoke-Json Post "https://api.fabric.microsoft.com/v1/workspaces/$WorkspaceId/reports" $token $body
$result = Wait-FabricOperation $response $token
$report = $result.Content | ConvertFrom-Json
if (-not $report.id) { throw 'Create report did not return an id.' }

Write-Output $report.id
Write-Host "Set Fabric:ReportId to $($report.id)."
