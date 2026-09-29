[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Pptx,
    [Parameter(Mandatory)][string]$Receipt,
    [switch]$ImplementationV2
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $Receipt) { throw 'Use a new editability receipt path.' }
$copy = Join-Path (Split-Path $Receipt) ('editability-' + [guid]::NewGuid().ToString('N') + '.pptx')
Copy-Item -LiteralPath $Pptx -Destination $copy
$app = $null
$pres = $null
$records = @()
try {
    $app = New-Object -ComObject PowerPoint.Application
    $pres = $app.Presentations.Open($copy, $false, $false, $false)
    $cases = if ($ImplementationV2) { @(@{slide=2;node='node-broker'}, @{slide=2;node='node-app'}) }
        else { @(@{slide=3;node='node-broker'}, @{slide=7;node='node-auth'}) }
    foreach ($case in $cases) {
        $slide = $pres.Slides.Item($case.slide)
        $node = $slide.Shapes.Item($case.node)
        $label = $node.GroupItems.Item($case.node + '-label')
        $oldLeft = [double]$node.Left
        $oldTop = [double]$node.Top
        $oldText = [string]$label.TextFrame.TextRange.Text
        $oldLabelLeft = [double]$label.Left
        $oldLabelTop = [double]$label.Top
        $attached = @()
        foreach ($shape in $slide.Shapes) {
            if ($shape.Connector -eq -1) {
                $cf = $shape.ConnectorFormat
                if (($cf.BeginConnected -eq -1 -and $cf.BeginConnectedShape.Name -eq $case.node) -or
                    ($cf.EndConnected -eq -1 -and $cf.EndConnectedShape.Name -eq $case.node)) {
                    $attached += [string]$shape.Name
                }
            }
        }
        if ($attached.Count -lt 1) { throw 'Expected attached connectors not found.' }
        $node.Left = $oldLeft + 12
        $node.Top = $oldTop + 6
        $label.TextFrame.TextRange.Text = $oldText + ' (edit)'
        foreach ($name in $attached) {
            $shape = $slide.Shapes.Item($name)
            $cf = $shape.ConnectorFormat
            if (-not (($cf.BeginConnected -eq -1 -and $cf.BeginConnectedShape.Name -eq $case.node) -or
                      ($cf.EndConnected -eq -1 -and $cf.EndConnectedShape.Name -eq $case.node))) {
                throw "Connector detached after edit: $name"
            }
        }
        if ([math]::Abs(($node.Left - $oldLeft) - 12) -gt 0.1 -or
            [math]::Abs(($node.Top - $oldTop) - 6) -gt 0.1 -or
            $label.TextFrame.TextRange.Text -ne ($oldText + ' (edit)')) {
            throw 'PowerPoint did not apply the node movement or label edit.'
        }
        if ([math]::Abs(($label.Left - $oldLabelLeft) - 12) -gt 0.1 -or
            [math]::Abs(($label.Top - $oldLabelTop) - 6) -gt 0.1) {
            throw 'The grouped label did not move with its container.'
        }
        $records += [ordered]@{
            slide=$case.slide; node=$case.node; editor='Microsoft PowerPoint desktop COM';
            movedPoints=@(12,6); groupedLabelMoved=$true; labelEdited=$true;
            connectorsRetained=$attached; method='Actual node movement, text edit and automatic connector attachment in an editable staged copy; no manual reroute'
        }
        $node.Left=$oldLeft
        $node.Top=$oldTop
        $label.TextFrame.TextRange.Text=$oldText
    }
    $pres.Save()
    [ordered]@{
        sourcePptx=$Pptx; sourceSha256=(Get-FileHash -LiteralPath $Pptx -Algorithm SHA256).Hash;
        passed=$true; actions=$records
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Receipt -Encoding utf8
    Write-Output 'Native node/group movement, label edits and attached connectors succeeded on both architecture slides.'
}
finally {
    if ($pres) { $pres.Close(); [void][Runtime.InteropServices.Marshal]::ReleaseComObject($pres) }
    if ($app) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($app) }
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
    Remove-Item -LiteralPath $copy
}
