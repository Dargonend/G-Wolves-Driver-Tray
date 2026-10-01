# ============================================================================
#  release.ps1 - build the distributable package
#
#  Does the whole release in one go:
#      compile -> self test -> assemble dist\GWMouseBattery-v<ver>\ -> zip
#
#  Usage:
#      powershell -ExecutionPolicy Bypass -File release.ps1
#      powershell -ExecutionPolicy Bypass -File release.ps1 -Version 1.0.1
#      powershell -ExecutionPolicy Bypass -File release.ps1 -SkipBuild
#
#  Layout of the staged package (the exe deliberately sits at the TOP so that
#  a downloader can just double-click it, instead of digging into bin\):
#
#      GWMouseBattery-v1.0.0\
#          GWMouseBattery.exe
#          使用说明.txt
#          启动.bat  menu.txt
#          README.md  协议说明.md
#          tools\    FindDPI.bat  Sniff.bat  finddpi.ps1  *note.txt
#          source\   src\*.cs  build.ps1  app.ico  tools\makeicon.cs  docs\
# ============================================================================

param([string] $Version = '1.0.0', [switch] $SkipBuild)

$ErrorActionPreference = 'Stop'

if ($env:PATHEXT -notlike '*.EXE*') {
    $env:PATHEXT = '.COM;.EXE;.BAT;.CMD;.VBS;.JS;.WS;.MSC'
}

$root = $PSScriptRoot
if (-not $root) { $root = Split-Path -Parent $MyInvocation.MyCommand.Path }

$dist = Join-Path $root 'dist'
$name = "GWMouseBattery-v$Version"
$pkg  = Join-Path $dist $name
$zip  = Join-Path $dist "$name.zip"

# --- 1. compile + self test ------------------------------------------------
if (-not $SkipBuild) {
    Write-Host "--- building ---" -ForegroundColor Cyan
    & (Join-Path $root 'build.ps1') -Root $root
    if ($LASTEXITCODE -ne 0) { throw "build or self test failed (exit $LASTEXITCODE)" }
}

$exe = Join-Path $root 'bin\GWMouseBattery.exe'
if (-not (Test-Path $exe)) { throw "missing $exe - run a build first" }

# --- 2. fresh staging directory -------------------------------------------
Write-Host "--- staging $name ---" -ForegroundColor Cyan
if (Test-Path $pkg) {
    try {
        Remove-Item $pkg -Recurse -Force
    } catch {
        Write-Host ''
        Write-Host '  [ERROR] Cannot replace the staging folder - a file inside it is locked.' -ForegroundColor Red
        Write-Host "          $pkg"
        Write-Host ''
        Write-Host '  The usual cause: the tray app is running FROM the packaging folder.'
        Write-Host '  Quit it first, then run this script again:'
        Write-Host ''
        Write-Host '      GWMouseBattery.exe --quit'
        Write-Host ''
        throw 'staging folder is locked'
    }
}
foreach ($sub in @('', 'tools', 'source', 'source\src', 'source\tools', 'source\docs')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $pkg $sub) | Out-Null
}

# --- 3. payload ------------------------------------------------------------
Copy-Item $exe $pkg -Force
Copy-Item (Join-Path $root '启动.bat') $pkg -Force
Copy-Item (Join-Path $root 'menu.txt') $pkg -Force
Copy-Item (Join-Path $root 'README.md') $pkg -Force
Copy-Item (Join-Path $root 'docs\协议说明.md') $pkg -Force

$toolDir = Join-Path $pkg 'tools'
# the diagnostic scripts are renamed to ASCII: cmd.exe would need the Chinese
# name embedded in another .bat to call them, and .bat files must stay ASCII.
Copy-Item (Join-Path $root '查DPI.bat')       (Join-Path $toolDir 'FindDPI.bat') -Force
Copy-Item (Join-Path $root '抓包.bat')         (Join-Path $toolDir 'Sniff.bat')   -Force
Copy-Item (Join-Path $root 'finddpi.ps1')     $toolDir -Force
Copy-Item (Join-Path $root 'finddpinote.txt') $toolDir -Force
Copy-Item (Join-Path $root 'sniffnote.txt')   $toolDir -Force

$srcDir = Join-Path $pkg 'source'
Copy-Item (Join-Path $root 'src\*.cs') (Join-Path $srcDir 'src') -Force
Copy-Item (Join-Path $root 'build.ps1') $srcDir -Force
Copy-Item (Join-Path $root 'app.ico')   $srcDir -Force
Copy-Item (Join-Path $root 'tools\makeicon.cs') (Join-Path $srcDir 'tools') -Force
Copy-Item (Join-Path $root 'docs\协议说明.md') (Join-Path $srcDir 'docs') -Force


# --- 5. 使用说明.txt: UTF-8 WITH BOM + CRLF (so Notepad shows Chinese) ------
$docPath = Join-Path $pkg '使用说明.txt'
$doc = [System.IO.File]::ReadAllText((Join-Path $root '使用说明.txt'), [System.Text.Encoding]::UTF8)
$doc = $doc.Replace("`r`n", "`n").Replace("`n", "`r`n")

# keep the hash printed in the doc in sync with what actually ships.
# NOTE: no "$" anchor here. The text is CRLF already, and in .NET regex a
# multiline "$" sits before the "\n", i.e. AFTER the "\r" - so an anchored
# pattern silently matches nothing and the doc keeps a stale hash.
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
$doc = [regex]::Replace($doc, '(?m)^( {6})[0-9A-Fa-f]{64}', ('${1}' + $hash))
if ($doc.IndexOf($hash) -lt 0) {
    throw "failed to stamp the SHA256 into 使用说明.txt (expected $hash)"
}
[System.IO.File]::WriteAllText($docPath, $doc, (New-Object System.Text.UTF8Encoding($true)))

# --- 6. zip ----------------------------------------------------------------
Add-Type -AssemblyName System.IO.Compression.FileSystem
if (Test-Path $zip) { Remove-Item $zip -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $pkg, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $true)

Write-Host ''
Write-Host "package : $pkg" -ForegroundColor Green
Write-Host "zip     : $zip" -ForegroundColor Green
Write-Host ("size    : {0:N1} KB" -f ((Get-Item $zip).Length / 1KB)) -ForegroundColor Green
Write-Host "exe sha : $hash"