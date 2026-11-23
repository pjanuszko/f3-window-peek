param([switch]$Desktop)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /warnaserror+ "/out:$PSScriptRoot\PreviewSessionTests.exe" (Join-Path $PSScriptRoot 'PreviewSession.cs') (Join-Path $PSScriptRoot 'PreviewSessionTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Kompilacja testów nie powiodła się.' }
& "$PSScriptRoot\PreviewSessionTests.exe"
if ($LASTEXITCODE -ne 0) { throw 'Testy nie przeszły.' }
& $compiler /nologo /target:exe /platform:x64 /optimize+ /warnaserror+ /reference:System.Drawing.dll "/out:$PSScriptRoot\PreviewLayoutTests.exe" (Join-Path $PSScriptRoot 'PreviewLayout.cs') (Join-Path $PSScriptRoot 'PreviewLayoutTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Kompilacja testów układu nie powiodła się.' }
& "$PSScriptRoot\PreviewLayoutTests.exe"
if ($LASTEXITCODE -ne 0) { throw 'Testy układu nie przeszły.' }
if ($Desktop) {
    $sourceFiles = @('F3WindowPeek.cs', 'PreviewSession.cs', 'FastPreview.cs', 'PreviewLayout.cs', 'AnimationPulse.cs', 'VirtualDesktops.cs', 'DragPreviewWindow.cs', 'SelectionVerification.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
    & $compiler /nologo /target:exe /main:SelectionVerification /platform:x64 /optimize+ /warnaserror+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/out:$PSScriptRoot\SelectionVerification.exe" $sourceFiles
    if ($LASTEXITCODE -ne 0) { throw 'Kompilacja testów wyboru okna nie powiodła się.' }
    & "$PSScriptRoot\SelectionVerification.exe" "$PSScriptRoot\selection-verified.log"
    Get-Content -LiteralPath "$PSScriptRoot\selection-verified.log"
    if ($LASTEXITCODE -ne 0) { throw 'Testy wyboru okna nie przeszły.' }
}
