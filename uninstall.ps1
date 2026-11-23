$ErrorActionPreference = 'Stop'
$executable = Join-Path $PSScriptRoot 'F3WindowPeek.exe'
if (Test-Path -LiteralPath $executable) {
    $stopper = Start-Process -FilePath $executable -ArgumentList '--stop' -WindowStyle Hidden -PassThru
    $stopper.WaitForExit()
}
$startup = [Environment]::GetFolderPath('Startup')
if (-not [string]::IsNullOrWhiteSpace($startup)) {
    $linkPath = Join-Path $startup 'F3 Window Peek.lnk'
    if (Test-Path -LiteralPath $linkPath) {
        $shell = New-Object -ComObject WScript.Shell
        $link = $shell.CreateShortcut($linkPath)
        if ($link.TargetPath -ne $executable) { throw 'Skrót wskazuje inny program; pozostawiono go bez zmian.' }
        Remove-Item -LiteralPath $linkPath
        Write-Output 'Autostart usunięty.'
    }
}
Write-Output 'F3 Window Peek wyłączony. Pliki programu pozostają w tym folderze.'
