<#
.SYNOPSIS
    Drive PCSX2 to replay a .gs dump in batch SW-renderer mode and harvest only
    the final-FRAME PNG(s) (SaveFrame), NOT the per-draw RT flood (SaveRT off).

.DESCRIPTION
    Same INI-patch/backup/restore mechanism as capture_pcsx2_rt_dumps.ps1, but
    sets SaveRT=false so we get PCSX2's GS-native final frame without writing
    one PNG per draw (which is ~50k files for the THAW canonical dump). Use this
    when you only need PCSX2's reference output for whole-frame comparison
    (brightness/tint), not a per-draw bisect.

    The INI defaults to PCSX2_INI or the current user's Documents/PCSX2 path.
    The executable defaults to PCSX2_EXE, then pcsx2-qt.exe on PATH.

.EXAMPLE
    .\capture_pcsx2_frame_only.ps1 -GsDump '...20260507234126.gs' -OutDir 'TestOutput\pcsx2_frame_20260507234126'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $GsDump,
    [Parameter(Mandatory = $true)][string] $OutDir,
    [string] $Ini = $(if ($env:PCSX2_INI) { $env:PCSX2_INI } else { Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'PCSX2\inis\PCSX2.ini' }),
    [string] $PcsX2Exe = $env:PCSX2_EXE,
    [int] $DurationSeconds = 18
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($PcsX2Exe)) {
    $pcsx2Command = Get-Command 'pcsx2-qt.exe' -ErrorAction SilentlyContinue
    if ($pcsx2Command) { $PcsX2Exe = $pcsx2Command.Source }
    else { throw 'Pass -PcsX2Exe or set PCSX2_EXE (pcsx2-qt.exe was not found on PATH).' }
}
function Resolve-RequiredPath { param([string]$Path,[string]$Desc)
    if (-not (Test-Path -LiteralPath $Path)) { throw "$Desc not found: $Path" }
    return (Resolve-Path -LiteralPath $Path).Path }
$GsDumpAbs = Resolve-RequiredPath $GsDump 'GS dump'
$IniAbs = Resolve-RequiredPath $Ini 'PCSX2 INI'
$PcsX2Abs = Resolve-RequiredPath $PcsX2Exe 'PCSX2 binary'
$OutDirAbs = if (Test-Path -LiteralPath $OutDir) { (Resolve-Path -LiteralPath $OutDir).Path } else { (New-Item -ItemType Directory -Force -Path $OutDir).FullName }
Write-Host "GS dump : $GsDumpAbs"
Write-Host "OutDir  : $OutDirAbs"
$timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
$IniBackup = "$IniAbs.bak.$timestamp"
Copy-Item -LiteralPath $IniAbs -Destination $IniBackup -Force
Write-Host "INI backed up to: $IniBackup"
$lines = Get-Content -LiteralPath $IniAbs -Encoding UTF8
$keysToSet = [ordered] @{
    'Renderer'        = '13'      # SW renderer
    'DumpGSData'      = 'true'
    'SaveRT'          = 'false'   # NO per-draw flood
    'SaveFrame'       = 'true'    # final frame only
    'SaveTexture'     = 'false'
    'SaveDepth'       = 'false'
    'SWDumpDirectory' = $OutDirAbs
    'HWDumpDirectory' = $OutDirAbs
}
$inSection = $false; $writtenKeys = @{}; $out = [System.Collections.Generic.List[string]]::new()
foreach ($raw in $lines) {
    $line = $raw
    if ($line -match '^\s*\[(.+?)\]\s*$') {
        if ($inSection) { foreach ($k in $keysToSet.Keys) { if (-not $writtenKeys.ContainsKey($k)) { $out.Add("$k = $($keysToSet[$k])"); $writtenKeys[$k]=$true } } }
        $inSection = ($matches[1].Trim() -eq 'EmuCore/GS'); $out.Add($line); continue
    }
    if ($inSection -and ($line -match '^\s*([A-Za-z0-9_]+)\s*=')) {
        $key = $matches[1]
        if ($keysToSet.Contains($key)) { $out.Add("$key = $($keysToSet[$key])"); $writtenKeys[$key]=$true; continue }
    }
    $out.Add($line)
}
$keysMissing = $keysToSet.Keys | Where-Object { -not $writtenKeys.ContainsKey($_) }
if ($keysMissing.Count -gt 0) {
    $sectionSeen = $out | Where-Object { $_ -match '^\s*\[EmuCore/GS\]\s*$' } | Select-Object -First 1
    if (-not $sectionSeen) { $out.Add(''); $out.Add('[EmuCore/GS]') }
    foreach ($k in $keysMissing) { $out.Add("$k = $($keysToSet[$k])") }
}
Set-Content -LiteralPath $IniAbs -Value $out -Encoding UTF8
Write-Host "INI patched (SaveFrame only, SaveRT off)."
$cleanupNeeded = $true
function Restore-Ini { if ($script:cleanupNeeded) { Write-Host "Restoring original INI..."; Copy-Item -LiteralPath $IniBackup -Destination $IniAbs -Force; $script:cleanupNeeded = $false } }
try {
    Write-Host "Spawning PCSX2 in batch mode for $DurationSeconds s..."
    $proc = Start-Process -FilePath $PcsX2Abs -ArgumentList @('-batch','-nogui','--',$GsDumpAbs) -PassThru -WindowStyle Hidden
    $deadline = [DateTime]::UtcNow.AddSeconds($DurationSeconds)
    while ([DateTime]::UtcNow -lt $deadline -and -not $proc.HasExited) { Start-Sleep -Milliseconds 500 }
    if (-not $proc.HasExited) { Write-Host "Terminating PCSX2 (PID $($proc.Id))."; Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue; $proc.WaitForExit(5000) | Out-Null }
    else { Write-Host "PCSX2 exited with code $($proc.ExitCode)." }
} finally { Restore-Ini }
$frames = Get-ChildItem -LiteralPath $OutDirAbs -Filter '*.png' -ErrorAction SilentlyContinue
Write-Host "Frame PNGs written: $($frames.Count)"
$frames | Select-Object -First 10 Name | ForEach-Object { Write-Host "  $($_.Name)" }
