param([string]$Out = "", [string]$ProbeDpi = "")

$ErrorActionPreference = 'Continue'

# Working directory = where this script lives (temp dumps + report go there).
# PowerShell 2.0 (Windows 7) has no $PSScriptRoot, hence the fallback.
if (-not $Out) { $Out = $PSScriptRoot }
if (-not $Out) { $Out = Split-Path -Parent $MyInvocation.MyCommand.Definition }
if (-not $Out) { $Out = "." }

# Normalise the directory. A trailing dot (or a stray ".\") breaks Remove-Item
# later on: Test-Path happily reports such a path as existing, but the file
# system provider then refuses to delete through it and blames the parent
# directory. Measured on Windows 11 / PowerShell 5.1.
if (Test-Path $Out) { $Out = (Resolve-Path $Out).Path }

# Locate the exe without caring about the layout: it may sit next to this
# script, in a bin\ subfolder, or one level up (packaged release puts the
# diagnostics in tools\ and the exe at the package root).
$exe = ""
foreach ($cand in @(
        (Join-Path $Out 'GWMouseBattery.exe'),
        (Join-Path $Out 'bin\GWMouseBattery.exe'),
        (Join-Path $Out '..\GWMouseBattery.exe'),
        (Join-Path $Out '..\bin\GWMouseBattery.exe'))) {
    if (Test-Path $cand) { $exe = (Resolve-Path $cand).Path; break }
}
if ($exe -eq "") {
    Write-Host "!! GWMouseBattery.exe not found near: $Out"
    exit 1
}
Write-Host ("exe  : " + $exe)
Write-Host ("out  : " + $Out)

$Regions = @(
    @{ Name = '通用设置区 0x0000';  Addr = 0;    Len = 260 },
    @{ Name = 'DPI 表区 0x1B00';    Addr = 6912; Len = 64  },
    @{ Name = 'DPI 表邻区 0x1A00';  Addr = 6656; Len = 64  }
)

function Invoke-Dump {
    param([int]$Addr, [int]$Len, [string]$Tag)
    $dumpFile = Join-Path $Out ("tmpdump-" + $Tag + "-" + $Addr + ".txt")
    if (Test-Path $dumpFile) { Remove-Item $dumpFile -Force }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = "--eeprom $Addr $Len --out `"$dumpFile`""
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $p.StandardOutput.ReadToEnd() | Out-Null
    $p.StandardError.ReadToEnd() | Out-Null
    $p.WaitForExit(40000) | Out-Null
    $map = @{}
    if (Test-Path $dumpFile) {
        foreach ($line in [System.IO.File]::ReadAllLines($dumpFile, [System.Text.Encoding]::UTF8)) {
            if ($line -match '^\s*([0-9A-Fa-f]{5})\s*:\s*(.*)$') {
                # the tool prints dump offsets in DECIMAL
                $base = [Convert]::ToInt32($matches[1], 10)
                $toks = ($matches[2].Trim()) -split '\s+'
                $i = 0
                foreach ($t in $toks) {
                    if ($t -match '^[0-9A-Fa-f]{2}$') {
                        $map[$base + $i] = [Convert]::ToInt32($t, 16)
                        $i++
                    }
                }
            }
        }
    }
    return $map
}

function Dump-All {
    param([string]$Tag)
    $all = @{}
    foreach ($r in $Regions) {
        $m = Invoke-Dump -Addr $r.Addr -Len $r.Len -Tag $Tag
        foreach ($k in $m.Keys) { $all[$k] = $m[$k] }
        Write-Host ("   " + $r.Name + " : " + $m.Count + " bytes")
    }
    return $all
}

function Hex-At {
    param($Map, [int]$Start, [int]$Count)
    $parts = @()
    for ($i = 0; $i -lt $Count; $i++) {
        $k = $Start + $i
        if ($Map.ContainsKey($k)) { $parts += ('{0:X2}' -f $Map[$k]) } else { $parts += '??' }
    }
    return ($parts -join ' ')
}

function Decode-6 {
    param($Map, [int]$Off)
    $vals = @()
    for ($i = 0; $i -lt 6; $i++) { if ($Map.ContainsKey($Off + $i)) { $vals += $Map[$Off + $i] } else { return '??' } }
    $sum = 0
    for ($i = 0; $i -lt 5; $i++) { $sum += $vals[$i] }
    $crc = (85 - $sum) -band 0xFF
    $code = $vals[0] + ($vals[1] * 256) + ((($vals[4] -shr 2) -band 3) * 65536)
    $dpi = $code + 1
    $ok = 'OK'
    if ($crc -ne $vals[5]) { $ok = 'CRC-BAD' }
    return ("{0} DPI (code={1}, crc {2})" -f $dpi, $code, $ok)
}

Write-Host ""
Write-Host "=== Step 1/2 : dumping BEFORE ==="
$before = Dump-All -Tag 'before'
if ($before.Count -eq 0) {
    Write-Host "!! Dump FAILED - mouse not reachable. Move the mouse and retry." -ForegroundColor Red
    exit 1
}
Write-Host ("   total " + $before.Count + " bytes (before)")
Write-Host ""
Write-Host "   0x1B00 region now: " (Hex-At -Map $before -Start 6912 -Count 42)
Write-Host ""

Write-Host "============================================================"
Write-Host "  NOW GO TO THE WEB DRIVER AND CHANGE THE DPI."
Write-Host "    800  ->  1600   (wait for the spinner to finish)"
Write-Host "    1600  ->  2400   (wait)"
Write-Host "    2400  ->  800   (wait)"
Write-Host ""
Write-Host "  Do NOT touch SPDT, polling rate, or anything else."
Write-Host "============================================================"
Write-Host ""
if ($ProbeDpi -ne "") {
    Write-Host ("   [probe] writing DPI " + $ProbeDpi + " with our own tool")
    $pout = Join-Path $Out 'tmpdump-probe.txt'
    $psi2 = New-Object System.Diagnostics.ProcessStartInfo
    $psi2.FileName = $exe
    $psi2.Arguments = "--dpi $ProbeDpi --out `"$pout`""
    $psi2.UseShellExecute = $false
    $psi2.RedirectStandardOutput = $true
    $psi2.RedirectStandardError = $true
    $pp = [System.Diagnostics.Process]::Start($psi2)
    $pp.StandardOutput.ReadToEnd() | Out-Null
    $pp.StandardError.ReadToEnd() | Out-Null
    $pp.WaitForExit(20000) | Out-Null
    if (Test-Path $pout) { Get-Content $pout -Encoding UTF8 | ForEach-Object { Write-Host ("      " + $_) } }
    Start-Sleep -Milliseconds 1500
} else {
    Read-Host "  Press Enter here AFTER all three changes are done"
}
Write-Host ""

Write-Host "=== Step 2/2 : dumping AFTER ==="
$after = Dump-All -Tag 'after'
Write-Host ("   total " + $after.Count + " bytes (after)")
Write-Host ""

$changed = @()
foreach ($k in $before.Keys) {
    if ($after.ContainsKey($k)) {
        if ($before[$k] -ne $after[$k]) { $changed += $k }
    }
}
$changed = $changed | Sort-Object

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("DPI 变化对比报告")
[void]$sb.AppendLine("==================================================")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("changed byte count : " + $changed.Count)
[void]$sb.AppendLine("")
if ($changed.Count -gt 0) {
    [void]$sb.AppendLine("--- individual changed bytes ---")
    foreach ($k in $changed) {
        [void]$sb.AppendLine((" addr {0,6} (0x{1:X4}) : {2:X2} -> {3:X2}" -f $k, $k, $before[$k], $after[$k]))
    }
}
[void]$sb.AppendLine("")
[void]$sb.AppendLine("--- region 0x1B00 (6912), 42 bytes BEFORE ---")
[void]$sb.AppendLine("  " + (Hex-At -Map $before -Start 6912 -Count 42))
[void]$sb.AppendLine("--- region 0x1B00 (6912), 42 bytes AFTER  ---")
[void]$sb.AppendLine("  " + (Hex-At -Map $after -Start 6912 -Count 42))
[void]$sb.AppendLine("")
[void]$sb.AppendLine("--- decoding 0x1B00 as 6-byte stages (AFTER) ---")
for ($s = 0; $s -lt 7; $s++) {
    $off = 6912 + $s * 6
    [void]$sb.AppendLine(("  stage {0} @ {1} : {2}  = {3}" -f ($s + 1), $off, (Hex-At -Map $after -Start $off -Count 6), (Decode-6 -Map $after -Off $off)))
}
[void]$sb.AppendLine("")
[void]$sb.AppendLine("--- region 0x0000, 44 bytes BEFORE ---")
[void]$sb.AppendLine("  " + (Hex-At -Map $before -Start 0 -Count 44))
[void]$sb.AppendLine("--- region 0x0000, 44 bytes AFTER  ---")
[void]$sb.AppendLine("  " + (Hex-At -Map $after -Start 0 -Count 44))
[void]$sb.AppendLine("")
[void]$sb.AppendLine("--- region 0x1A00, 64 bytes BEFORE ---")
[void]$sb.AppendLine("  " + (Hex-At -Map $before -Start 6656 -Count 64))
[void]$sb.AppendLine("--- region 0x1A00, 64 bytes AFTER  ---")
[void]$sb.AppendLine("  " + (Hex-At -Map $after -Start 6656 -Count 64))

$report = Join-Path $Out 'dpidiff.txt'
[System.IO.File]::WriteAllText($report, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))

Write-Host ("RESULT: " + $changed.Count + " bytes changed.  Report -> dpidiff.txt")
