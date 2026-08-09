<#
.SYNOPSIS
  Capture PCSX2 SW-native replay frames for a list of THAW .gs dumps, pruning each
  output dir to just the rt0_00000 (native FBP0 scene) dumps to save disk.
  Used for the native-reference re-baseline (grade our FBP0 against the true GS output
  instead of the biased embedded HW screenshot).

  Configuration: PCSX2_SNAPS_DIR (or -SnapDir), PCSX2_INI (or -Ini), and
  PCSX2_EXE (or -PcsX2Exe).
#>
[CmdletBinding()]
param(
    [string] $SnapDir = $(if ($env:PCSX2_SNAPS_DIR) { $env:PCSX2_SNAPS_DIR } else { Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'PCSX2\snaps' }),
    [string] $OutRoot = 'TestOutput',
    [int]    $DurationSeconds = 12,
    [string[]] $SkipTags = @('143551', '233835'),
    [string] $Ini = $env:PCSX2_INI,
    [string] $PcsX2Exe = $env:PCSX2_EXE
)
$ErrorActionPreference = 'Continue'
$script = Join-Path $PSScriptRoot 'capture_pcsx2_frame_only.ps1'
$gsFiles = Get-ChildItem -LiteralPath $SnapDir -Filter '*.gs'
foreach ($gs in $gsFiles) {
    if ($gs.Name -notmatch '_(\d{14})\.gs$') { continue }
    $full = $matches[1]
    $tag = $full.Substring($full.Length - 6)
    if ($SkipTags -contains $tag) { Write-Host "SKIP $tag (already have native)"; continue }
    $outDir = Join-Path $OutRoot "pcsx2_native_$tag"
    Write-Host "=== CAPTURE $tag -> $outDir ==="
    $captureArgs = @{
        GsDump = $gs.FullName
        OutDir = $outDir
        DurationSeconds = $DurationSeconds
    }
    if ($Ini) { $captureArgs.Ini = $Ini }
    if ($PcsX2Exe) { $captureArgs.PcsX2Exe = $PcsX2Exe }
    & $script @captureArgs *>&1 |
        Select-Object -Last 2 | ForEach-Object { Write-Host "  $_" }
    # prune: keep only the native FBP0 scene dumps
    Get-ChildItem -LiteralPath $outDir -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notlike '*_rt0_00000_C_32.png' } |
        Remove-Item -Force -ErrorAction SilentlyContinue
    $kept = (Get-ChildItem -LiteralPath $outDir -File -ErrorAction SilentlyContinue | Measure-Object).Count
    Write-Host "  pruned -> $kept rt0 files"
}
Write-Host "ALL NATIVE CAPTURES DONE"
