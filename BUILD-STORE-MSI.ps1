param(
    [string]$Version = '1.1.0',
    [switch]$SkipWixInstall
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = $PSScriptRoot
$payload = Join-Path $root 'store\payload'
$dist = Join-Path $root 'dist'
$project = Join-Path $root 'src\FolderSizeMeter.csproj'
$wxs = Join-Path $root 'installer\Product.wxs'

function Assert-LastExitCode([string]$Message) {
    if ($LASTEXITCODE -ne 0) { throw "$Message (exit code $LASTEXITCODE)" }
}

Write-Host ''
Write-Host '=== Juicy Folder Meter - Microsoft Store MSI build ===' -ForegroundColor Cyan
Write-Host "Version: $Version"

if (-not (Get-Command dotnet.exe -ErrorAction SilentlyContinue)) {
    throw '.NET SDK 8 or later is required. Install the .NET 8 SDK, then run this file again.'
}

$sdks = & dotnet --list-sdks
if (-not ($sdks -match '^8\.')) {
    throw '.NET SDK 8.x was not found. Install the .NET 8 SDK, then run this file again.'
}

# Build the native x64 overlay DLL using the already-tested project build.
Write-Host '[1/4] Building native Explorer overlay...' -ForegroundColor Yellow
& (Join-Path $root 'Build.ps1')
Assert-LastExitCode 'Base/native build failed.'

Remove-Item -LiteralPath $payload -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $payload,$dist | Out-Null

# Publish as a self-contained single-file WinForms application: no .NET runtime prerequisite on customer PCs.
Write-Host '[2/4] Publishing self-contained x64 app...' -ForegroundColor Yellow
& dotnet publish $project `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -p:Version=$Version `
    -p:InformationalVersion=$Version `
    -p:FileVersion="$Version.0" `
    -p:AssemblyVersion="$Version.0" `
    -p:Product='Juicy Folder Meter' `
    -p:Company='Juicy Apps' `
    -p:Copyright='Copyright (c) 2026 Juicy Apps' `
    -o $payload
Assert-LastExitCode '.NET Store publish failed.'

Copy-Item -LiteralPath (Join-Path $root 'app\FolderSizeOverlay.dll') -Destination $payload -Force

# We intentionally install only the two production PE files.
$required = @('FolderSizeMeter.exe','FolderSizeOverlay.dll')
foreach ($name in $required) {
    $p = Join-Path $payload $name
    if (-not (Test-Path -LiteralPath $p)) { throw "Missing Store payload file: $name" }
}
Get-ChildItem -LiteralPath $payload -File | Where-Object { $_.Name -notin $required } | Remove-Item -Force

# WiX is used only to compile the MSI. First run can install the pinned WiX 6 tool automatically.
$wix = Get-Command wix.exe -ErrorAction SilentlyContinue
if (-not $wix) {
    if ($SkipWixInstall) { throw 'WiX was not found and -SkipWixInstall was specified.' }
    Write-Host '[3/4] WiX not found; installing WiX Toolset 6.0.2 as a .NET global tool...' -ForegroundColor Yellow
    & dotnet tool install --global wix --version 6.0.2
    if ($LASTEXITCODE -ne 0) {
        # It may already exist but not be on this PowerShell session PATH.
        & dotnet tool update --global wix --version 6.0.2
        Assert-LastExitCode 'Could not install/update the WiX .NET tool.'
    }
    $dotnetTools = Join-Path $env:USERPROFILE '.dotnet\tools'
    if ($env:PATH -notlike "*$dotnetTools*") { $env:PATH = "$dotnetTools;$env:PATH" }
    $wix = Get-Command wix.exe -ErrorAction SilentlyContinue
    if (-not $wix) { throw 'WiX was installed but wix.exe is not available in PATH. Open a new PowerShell window and run again.' }
} else {
    Write-Host '[3/4] WiX found.' -ForegroundColor Yellow
}

$msi = Join-Path $dist ("JuicyFolderMeter_{0}_x64_UNSIGNED.msi" -f $Version)
Remove-Item -LiteralPath $msi -Force -ErrorAction SilentlyContinue

Write-Host '[4/4] Building MSI...' -ForegroundColor Yellow
& wix build $wxs `
    -arch x64 `
    -d "ProductVersion=$Version" `
    -d "ProjectRoot=$root" `
    -d "PayloadDir=$payload" `
    -defaultcompressionlevel high `
    -pdbtype none `
    -out $msi
Assert-LastExitCode 'WiX MSI build failed.'

if (-not (Test-Path -LiteralPath $msi)) { throw 'MSI build finished without producing the expected file.' }

$hash = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash
Write-Host ''
Write-Host 'SUCCESS' -ForegroundColor Green
Write-Host "Unsigned test MSI: $msi"
Write-Host "SHA256: $hash"
Write-Host ''
Write-Host 'Important: Microsoft Store MSI/EXE submissions require the MSI AND every PE file inside it to be Authenticode-signed with a CA-trusted code-signing certificate.' -ForegroundColor Yellow
Write-Host 'Use SIGN-FOR-STORE.ps1 after you obtain/configure that certificate. Do not submit the _UNSIGNED.msi file.' -ForegroundColor Yellow
