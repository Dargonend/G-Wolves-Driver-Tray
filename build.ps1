# ============================================================================
#  G-Wolves Driver Tray - build script
#
#  ASCII only on purpose: cmd.exe / PowerShell 5.1 reinterpret non-ASCII bytes
#  in scripts according to the current code page, which silently corrupts them.
#
#  Requires nothing but the C# compiler that ships with .NET Framework 4.x
#  (C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe). No SDK, no NuGet.
# ============================================================================

param([string] $Root)

$ErrorActionPreference = 'Stop'

# This machine's shell may be launched with PATHEXT lacking .EXE, which makes
# PowerShell refuse to run any external program. Fix it up front.
if ($env:PATHEXT -notlike '*.EXE*') {
    $env:PATHEXT = '.COM;.EXE;.BAT;.CMD;.VBS;.JS;.WS;.MSC'
}

$root = $Root
if (-not $root) { $root = $PSScriptRoot }
if (-not $root) { $root = Split-Path -Parent $MyInvocation.MyCommand.Path }
$outDir = Join-Path $root 'bin'
$outExe = Join-Path $outDir 'G-Wolves-Driver-Tray.exe'

$sources = @(
    (Join-Path $root 'src\FenrirCore.cs'),
    (Join-Path $root 'src\FenrirTray.cs'),
    (Join-Path $root 'src\AssemblyInfo.cs')
)

$icon = Join-Path $root 'app.ico'

# --- source files must be UTF-8 WITH BOM, otherwise csc decodes them as ANSI
#     and every Chinese string turns into garbage -----------------------------
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
foreach ($file in $sources) {
    if (-not (Test-Path $file)) { throw "missing source file: $file" }

    $bytes = [System.IO.File]::ReadAllBytes($file)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    if (-not $hasBom) {
        # read with an EXPLICIT UTF-8 decoder; a bare ReadAllText would use GBK
        $text = [System.IO.File]::ReadAllText($file, [System.Text.Encoding]::UTF8)
        [System.IO.File]::WriteAllText($file, $text, $utf8Bom)
        Write-Host "added UTF-8 BOM: $file" -ForegroundColor DarkGray
    }
}

$cscCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw 'csc.exe not found (needs .NET Framework 4.x)' }

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

Write-Host "compiler : $csc" -ForegroundColor DarkGray
Write-Host "output   : $outExe" -ForegroundColor DarkGray
Write-Host ''

$cscArgs = @(
    '/nologo'
    '/target:winexe'
    '/platform:anycpu'
    '/optimize+'
    '/warn:4'
    '/preferreduilang:en-US'
    "/out:$outExe"
    '/reference:System.dll'
    '/reference:System.Drawing.dll'
    '/reference:System.Windows.Forms.dll'
) + ($(if (Test-Path $icon) { @("/win32icon:$icon") } else { @() })) + $sources

$output = & $csc $cscArgs 2>&1
$code = $LASTEXITCODE

$warnings = $output | Where-Object { $_ -match ': warning ' }
$errors   = $output | Where-Object { $_ -match ': error ' }

if ($warnings) {
    Write-Host '--- warnings ---' -ForegroundColor Yellow
    $warnings | ForEach-Object { Write-Host $_ -ForegroundColor Yellow }
}

if ($code -ne 0 -or $errors) {
    Write-Host '--- build FAILED ---' -ForegroundColor Red
    $output | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    exit 1
}

$size = (Get-Item $outExe).Length
Write-Host ("build OK  -> $outExe  ($([math]::Round($size/1KB,1)) KB)") -ForegroundColor Green

# --- self test (logic only, never touches hardware) -------------------------
Write-Host ''
$_oldOut = [Console]::OutputEncoding
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
& $outExe '--selftest'
$selftest = $LASTEXITCODE
try { [Console]::OutputEncoding = $_oldOut } catch { }

exit $selftest
