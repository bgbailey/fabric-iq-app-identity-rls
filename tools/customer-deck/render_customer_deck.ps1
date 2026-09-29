[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Pptx,
    [Parameter(Mandatory)][string]$OutDir
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $OutDir) { throw "Use a new render directory: $OutDir" }
$null = New-Item -ItemType Directory -Path $OutDir
$copy = Join-Path $OutDir ('render-' + [guid]::NewGuid().ToString('N') + '.pptx')
Copy-Item -LiteralPath $Pptx -Destination $copy
$prior = @(Get-Process POWERPNT -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
$app = $null
$pres = $null
try {
    $app = New-Object -ComObject PowerPoint.Application
    $pres = $app.Presentations.Open($copy, $true, $false, $false)
    $height = [int][math]::Round(1600 * $pres.PageSetup.SlideHeight / $pres.PageSetup.SlideWidth)
    for ($i = 1; $i -le $pres.Slides.Count; $i++) {
        $slide = $pres.Slides.Item($i)
        try { $slide.Export((Join-Path $OutDir ('slide-{0:d2}.png' -f $i)), 'PNG', 1600, $height) }
        finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($slide) }
    }
    Write-Output ("Rendered {0} slides to {1}" -f $pres.Slides.Count, $OutDir)
}
finally {
    if ($pres) {
        $pres.Close()
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($pres)
    }
    if ($app) {
        # Never quit a pre-existing PowerPoint instance holding the user's deck.
        if ($prior.Count -eq 0) { $app.Quit() }
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($app)
    }
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
    Remove-Item -LiteralPath $copy
}
