param([switch]$NoStartup)
$ErrorActionPreference = 'Stop'
$executable = Join-Path $PSScriptRoot 'F3WindowPeek.exe'
if (-not (Test-Path -LiteralPath $executable)) { & (Join-Path $PSScriptRoot 'build.ps1') }
if (-not $NoStartup) {
    $startup = [Environment]::GetFolderPath('Startup')
    if ([string]::IsNullOrWhiteSpace($startup)) { throw 'Nie znaleziono folderu Autostart.' }
    $linkPath = Join-Path $startup 'F3 Window Peek.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($linkPath)
    if ((Test-Path -LiteralPath $linkPath) -and $link.TargetPath -ne $executable) {
        throw "Skrót już istnieje i prowadzi do innego programu: $linkPath"
    }
    $link.TargetPath = $executable
    $link.WorkingDirectory = $PSScriptRoot
    $link.Description = 'Przytrzymaj F3: podgląd okien. Puść F3: powrót.'
    $link.WindowStyle = 7
    $link.Save()
    Write-Output "Autostart: $linkPath"
}
Start-Process -FilePath $executable -WorkingDirectory $PSScriptRoot -WindowStyle Hidden
Write-Output 'F3 Window Peek uruchomiony. Przytrzymaj F3, aby zobaczyć okna.'
