param([string]$Version = '1.1.0')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$msi = Join-Path $dist ("JuicyFolderMeter_{0}_x64_UNSIGNED.msi" -f $Version)
if (-not (Test-Path -LiteralPath $msi)) {
    $msi = Join-Path $dist ("JuicyFolderMeter_{0}_x64.msi" -f $Version)
}
if (-not (Test-Path -LiteralPath $msi)) { throw 'No MSI found. Run BUILD-STORE-MSI.cmd first.' }

$log = Join-Path $dist 'msi-install-test.log'
Write-Host 'Installing silently exactly as Microsoft Store validation does (/qn)...' -ForegroundColor Cyan
$p = Start-Process msiexec.exe -Verb RunAs -Wait -PassThru -ArgumentList @('/i', ('"'+$msi+'"'), '/qn', '/norestart', '/l*v', ('"'+$log+'"'))
if ($p.ExitCode -notin @(0,3010)) { throw "Silent install failed with exit code $($p.ExitCode). See $log" }

$installDir = Join-Path $env:ProgramFiles ("Juicy Apps\Juicy Folder Meter\$Version")
$exe = Join-Path $installDir 'FolderSizeMeter.exe'
$dll = Join-Path $installDir 'FolderSizeOverlay.dll'
if (-not (Test-Path -LiteralPath $exe)) { throw "Installed EXE missing: $exe" }
if (-not (Test-Path -LiteralPath $dll)) { throw "Installed overlay DLL missing: $dll" }

$overlayRoot = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers'
foreach ($name in @('FolderSizeMeter Orange','FolderSizeMeter Red')) {
    if (-not (Test-Path -LiteralPath (Join-Path $overlayRoot $name))) { throw "Overlay registry entry missing: $name" }
}

Write-Host 'PASS: silent installation, files and overlay registrations are present.' -ForegroundColor Green
if ($p.ExitCode -eq 3010) { Write-Host 'Windows Installer reported reboot-required (3010). The MSI suppresses automatic reboot.' -ForegroundColor Yellow }
Write-Host 'Sign out and sign back in before judging Explorer overlays, because Explorer must load the new shell extension.'
Write-Host "Install log: $log"
