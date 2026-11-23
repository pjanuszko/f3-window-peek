$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Brak kompilatora .NET Framework 4.x.' }
$sourceFiles = @('F3WindowPeek.cs', 'PreviewSession.cs', 'FastPreview.cs', 'PreviewLayout.cs', 'AnimationPulse.cs', 'VirtualDesktops.cs', 'DragPreviewWindow.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /warnaserror+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/out:$PSScriptRoot\F3WindowPeek.exe" $sourceFiles
if ($LASTEXITCODE -ne 0) { throw 'Kompilacja nie powiodła się.' }
Write-Output "Gotowe: $PSScriptRoot\F3WindowPeek.exe"
