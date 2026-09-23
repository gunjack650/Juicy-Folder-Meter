$ErrorActionPreference = 'Stop'
$project = $PSScriptRoot
$build = Join-Path $project 'build'
$app = Join-Path $project 'app'
New-Item -ItemType Directory -Force $build,$app | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Install Visual Studio C++ Build Tools and a Windows SDK.' }
Add-Type -AssemblyName System.Drawing
foreach ($item in @(@('orange', [System.Drawing.Color]::FromArgb(245,158,11)), @('red',[System.Drawing.Color]::FromArgb(220,38,38)))) {
    $stream = [IO.File]::Create((Join-Path $project ('native\' + $item[0] + '.ico')))
    $writer = [IO.BinaryWriter]::new($stream)
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]4)
    $images = @()
    foreach ($size in @(16,32,48,256)) {
        $bitmap = [Drawing.Bitmap]::new($size,$size)
        $g = [Drawing.Graphics]::FromImage($bitmap); $g.Clear([Drawing.Color]::Transparent)
        $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $brush = [Drawing.SolidBrush]::new($item[1]); $border = [Drawing.Pen]::new([Drawing.Color]::White, [float]($size / 40))
        $diameter = [float]($size * 0.29); $x = [float]($size * 0.04); $y = [float]($size * 0.66)
        $g.FillEllipse($brush,$x,$y,$diameter,$diameter); $g.DrawEllipse($border,$x,$y,$diameter,$diameter)
        $ms = [IO.MemoryStream]::new(); $bitmap.Save($ms,[Drawing.Imaging.ImageFormat]::Png); $images += ,$ms.ToArray()
        $ms.Dispose(); $border.Dispose(); $brush.Dispose(); $g.Dispose(); $bitmap.Dispose()
    }
    $offset = 6 + 4 * 16; $i = 0
    foreach ($size in @(16,32,48,256)) {
        $s = if($size -eq 256){0}else{$size}; $writer.Write([byte]$s); $writer.Write([byte]$s); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset); $offset += $images[$i].Length; $i++
    }
    foreach($bytes in $images){$writer.Write([byte[]]$bytes)}; $writer.Dispose()
}
$batch = @"
@echo off
call "$vs\VC\Auxiliary\Build\vcvars64.bat" >nul
cd /d "$project\native"
rc /nologo /fo "$build\Overlay.res" Overlay.rc
if errorlevel 1 exit /b 1
cl /nologo /O2 /W4 /WX /MT /EHsc /guard:cf /LD Overlay.cpp "$build\Overlay.res" /Fo"$build\Overlay.obj" /link /DEF:Overlay.def /OUT:"$app\FolderSizeOverlay.dll" /IMPLIB:"$build\Overlay.lib" /DYNAMICBASE /NXCOMPAT /guard:cf kernel32.lib user32.lib shell32.lib ole32.lib uuid.lib
if errorlevel 1 exit /b 1
cl /nologo /O2 /W4 /WX /MT /EHsc Probe.cpp /Fo"$build\Probe.obj" /link /OUT:"$app\OverlayProbe.exe" kernel32.lib user32.lib shell32.lib ole32.lib uuid.lib
exit /b %errorlevel%
"@
$batchPath = Join-Path $build 'native-build.cmd'
Set-Content -LiteralPath $batchPath -Value $batch -Encoding ascii
& $env:ComSpec /c $batchPath
if ($LASTEXITCODE -ne 0) { throw 'Native build failed.' }
dotnet publish (Join-Path $project 'src\FolderSizeMeter.csproj') -c Release --self-contained false -o $app
if ($LASTEXITCODE -ne 0) { throw '.NET build failed.' }
Write-Host "Built app in $app"
