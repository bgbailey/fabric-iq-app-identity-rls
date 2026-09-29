[CmdletBinding()]
param(
    [switch]$Build,
    [string]$DemoConfig,
    [switch]$AllowLive,
    [switch]$IqSignIn
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if ($AllowLive -and -not $DemoConfig) {
    throw '-AllowLive requires an approved external -DemoConfig. This script does not authorize cloud use.'
}
if ($IqSignIn -and (-not $DemoConfig -or $AllowLive)) {
    throw '-IqSignIn requires -DemoConfig and cannot be combined with -AllowLive.'
}
Push-Location $repo
try {
    if ($Build) {
        Push-Location (Join-Path $repo 'src\DemoWeb')
        try {
            & npm run build
            if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed. See src\DemoWeb\README.rst for prerequisites.' }
        }
        finally { Pop-Location }
        & dotnet build .\src\IqRls.Demo\IqRls.Demo.csproj --no-restore --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Backend build failed. Restore the pinned solution dependencies if missing.' }
    }
    $hostDll = Join-Path $repo 'src\IqRls.Demo\bin\Debug\net8.0\IqRls.Demo.dll'
    $index = Join-Path $repo 'src\DemoWeb\dist\index.html'
    if (-not (Test-Path -LiteralPath $hostDll) -or -not (Test-Path -LiteralPath $index)) {
        throw 'The demo is not built. Restore prerequisites, then run this script with -Build.'
    }
    $arguments = @($hostDll)
    if ($IqSignIn) { $arguments += '--iq-sign-in' }
    if ($DemoConfig) { $arguments += @('--demo-config', $DemoConfig) }
    if ($AllowLive) { $arguments += '--allow-live' }
    if ($IqSignIn) {
        Write-Host 'Explicit IQ sign-in will open your browser. Use the approved MCAPS DEV delegated account, not a corporate account.'
    }
    else {
        Write-Host 'Local demo: http://127.0.0.1:5187 (demo identity, not login)'
        if (-not $AllowLive) { Write-Host 'Cloud execution is disabled. No sample results will be substituted.' }
    }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Demo host exited with code $LASTEXITCODE." }
}
finally { Pop-Location }
