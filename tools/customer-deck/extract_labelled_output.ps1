[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$StageRoot,
    [Parameter(Mandatory)][string]$PublishedRoot,
    [Parameter(Mandatory)][string]$OutputRoot,
    [int]$Version = 1
)
$ErrorActionPreference='Stop'
$null=New-Item -ItemType Directory -Path $OutputRoot -Force
function Read-Blocks($Shapes) {
    $items=@()
    foreach ($shape in $Shapes) {
        if ($shape.Type -eq 6) {
            $items += @(Read-Blocks $shape.GroupItems)
            continue
        }
        $value=''
        if ($shape.HasTextFrame -eq -1 -and $shape.TextFrame.HasText -eq -1) {
            $value=$shape.TextFrame.TextRange.Text
        }
        $items += [ordered]@{
            type=[int]$shape.Type
            geom=@([math]::Round($shape.Left/72,2),[math]::Round($shape.Top/72,2),
                   [math]::Round($shape.Width/72,2),[math]::Round($shape.Height/72,2))
            text=$value; chart=[bool]($shape.HasChart -eq -1); table=[bool]($shape.HasTable -eq -1)
        }
    }
    return $items
}
$app=$null
try {
    $app=New-Object -ComObject PowerPoint.Application
    foreach ($file in @("Fabric_ISV_Identity_and_Analytics_v$Version.pptx","ISV_Security_Diagrams_v$Version.pptx")) {
        $models=@()
        foreach ($sourceRoot in @($StageRoot,$PublishedRoot)) {
            $source=Join-Path $sourceRoot $file
            $copy=Join-Path $OutputRoot ([guid]::NewGuid().ToString('N')+'.pptx')
            Copy-Item -LiteralPath $source -Destination $copy
            $pres=$null
            try {
                $pres=$app.Presentations.Open($copy,$true,$false,$false)
                $slides=@()
                foreach ($slide in $pres.Slides) {
                    $notes=''
                    foreach ($ns in $slide.NotesPage.Shapes) {
                        if ($ns.Type -eq 14 -and $ns.PlaceholderFormat.Type -eq 2 -and $ns.HasTextFrame -eq -1) {
                            $notes=$ns.TextFrame.TextRange.Text
                        }
                    }
                    $slides += [ordered]@{
                        n=[int]$slide.SlideIndex
                        title=$slide.Shapes.Item('Slide title').TextFrame.TextRange.Text
                        layout=$slide.CustomLayout.Name
                        notes=$notes
                        blocks=@(Read-Blocks $slide.Shapes)
                    }
                }
                $label=$pres.SensitivityLabel.GetLabel()
                $models += [ordered]@{
                    deck=$source;slide_w_in=[math]::Round($pres.PageSetup.SlideWidth/72,3)
                    slide_h_in=[math]::Round($pres.PageSetup.SlideHeight/72,3)
                    slides=$slides;labelName=$label.LabelName;labelId=$label.LabelId
                }
            }
            finally {
                if ($pres) { $pres.Close();[void][Runtime.InteropServices.Marshal]::ReleaseComObject($pres) }
                Remove-Item -LiteralPath $copy
            }
        }
        $expected=$models[0].slides | ConvertTo-Json -Depth 12 -Compress
        $actual=$models[1].slides | ConvertTo-Json -Depth 12 -Compress
        if ($expected -cne $actual) { throw "Labelled publication differs in slide text, geometry, blocks or notes: $file" }
        $out=Join-Path $OutputRoot ($file+'.model.json')
        $models[1] | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $out -Encoding utf8
        [pscustomobject]@{
            file=$file;slides=$models[1].slides.Count;publishedContentMatches=$true
            labelName=$models[1].labelName;model=$out
        } | ConvertTo-Json -Compress
    }
}
finally {
    if ($app) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($app) }
    [GC]::Collect();[GC]::WaitForPendingFinalizers()
}
