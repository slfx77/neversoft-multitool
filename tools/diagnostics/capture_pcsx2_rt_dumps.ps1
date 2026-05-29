<#
.SYNOPSIS
    Drive PCSX2 to replay a .gs dump in batch SW-renderer mode and harvest
    the per-draw RT / alpha PNGs into a directory matching the layout
    fb_state_bisect.py expects.

.DESCRIPTION
    Edits the supplied PCSX2 INI file in place under [EmuCore/GS]:
        Renderer = 13 (SW)
        DumpGSData = true, SaveRT = true, SaveFrame = true
        SWDumpDirectory = <OutDir>
        HWDumpDirectory = <OutDir>

    The original INI is backed up to <Ini>.bak.<timestamp> and restored when
    the script exits (including on Ctrl+C). PCSX2 is spawned via:
        pcsx2-qt.exe -batch -nogui -- <GsDump>

    PCSX2's dump replayer loops the same packet sequence indefinitely
    (s_dump_loop_count = -1). The script kills the process after
    -DurationSeconds, then reports the RT PNG count.

.PARAMETER GsDump
    Path to a PCSX2 .gs dump file to replay.

.PARAMETER OutDir
    Directory where PCSX2 writes the per-draw NNNNN_fFFFFF_rt[01]_BBBBB_C_NN.png
    snapshots. Created if it doesn't exist. Existing contents are NOT cleared
    so multiple invocations against different dumps can coexist.

.PARAMETER Ini
    Path to the PCSX2 INI file to modify. Defaults to the v1.7.5558 portable
    location documented in memory. Must point at a valid existing PCSX2 INI;
    the script refuses to create one from scratch.

.PARAMETER PcsX2Exe
    Path to pcsx2-qt.exe. Defaults to the v1.7.5558 binary documented in
    memory.

.PARAMETER DurationSeconds
    How long to let PCSX2 replay (default 25). Enough for ~3 loops over the
    canonical THAW dump on a fast machine. Increase if PCSX2 doesn't seem to
    finish writing all RT files.

.EXAMPLE
    .\capture_pcsx2_rt_dumps.ps1 `
        -GsDump 'C:\Users\mmc99\Documents\PCSX2\snaps\... 20260507234126.gs' `
        -OutDir 'TestOutput\pcsx2_gsdump_replay_20260507234126'
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $GsDump,

    [Parameter(Mandatory = $true)]
    [string] $OutDir,

    [string] $Ini = 'C:\Users\mmc99\Desktop\Games\Emulation\PS2\pcsx2-v1.7.5558-windows-x64-Qt\portable\inis\PCSX2.ini',

    [string] $PcsX2Exe = 'C:\Users\mmc99\Desktop\Games\Emulation\PS2\pcsx2-v1.7.5558-windows-x64-Qt\pcsx2-qt.exe',

    [int] $DurationSeconds = 25
)

$ErrorActionPreference = 'Stop'

function Resolve-RequiredPath {
    param([string] $Path, [string] $Description)
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Description not found: $Path"
    }
    return (Resolve-Path -LiteralPath $Path).Path
}

$GsDumpAbs = Resolve-RequiredPath $GsDump 'GS dump'
$IniAbs = Resolve-RequiredPath $Ini 'PCSX2 INI'
$PcsX2Abs = Resolve-RequiredPath $PcsX2Exe 'PCSX2 binary'

$OutDirAbs = if (Test-Path -LiteralPath $OutDir) {
    (Resolve-Path -LiteralPath $OutDir).Path
} else {
    (New-Item -ItemType Directory -Force -Path $OutDir).FullName
}

Write-Host "GS dump : $GsDumpAbs"
Write-Host "INI     : $IniAbs"
Write-Host "PCSX2   : $PcsX2Abs"
Write-Host "OutDir  : $OutDirAbs"

# Back up the original INI. .bak.<unix-ms> prevents collisions across repeated runs.
$timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
$IniBackup = "$IniAbs.bak.$timestamp"
Copy-Item -LiteralPath $IniAbs -Destination $IniBackup -Force
Write-Host "INI backed up to: $IniBackup"

# Build the modified INI in memory, then write atomically.
$lines = Get-Content -LiteralPath $IniAbs -Encoding UTF8

# The INI keys we need under [EmuCore/GS] section. PCSX2 1.7.x lower-cases the
# section header to [EmuCore/GS] and uses CamelCase keys with bool values
# encoded as `true` / `false`.
$keysToSet = [ordered] @{
    'Renderer'         = '13'          # SW renderer
    'DumpGSData'       = 'true'
    'SaveRT'           = 'true'
    'SaveFrame'        = 'true'
    'SWDumpDirectory'  = $OutDirAbs
    'HWDumpDirectory'  = $OutDirAbs
}

$sectionHeader = '[EmuCore/GS]'
$inSection = $false
$writtenKeys = @{}
$out = [System.Collections.Generic.List[string]]::new()

foreach ($raw in $lines) {
    $line = $raw

    if ($line -match '^\s*\[(.+?)\]\s*$') {
        # If we're leaving the target section without writing all keys, flush
        # them just before the new section header.
        if ($inSection) {
            foreach ($k in $keysToSet.Keys) {
                if (-not $writtenKeys.ContainsKey($k)) {
                    $out.Add("$k = $($keysToSet[$k])")
                    $writtenKeys[$k] = $true
                }
            }
        }
        $inSection = ($matches[1].Trim() -eq 'EmuCore/GS')
        $out.Add($line)
        continue
    }

    if ($inSection -and ($line -match '^\s*([A-Za-z0-9_]+)\s*=')) {
        $key = $matches[1]
        if ($keysToSet.Contains($key)) {
            $out.Add("$key = $($keysToSet[$key])")
            $writtenKeys[$key] = $true
            continue
        }
    }

    $out.Add($line)
}

# Append any keys never seen (section may have been absent or partial). If the
# section header itself was missing, create it.
$keysMissing = $keysToSet.Keys | Where-Object { -not $writtenKeys.ContainsKey($_) }
if ($keysMissing.Count -gt 0) {
    $sectionSeen = $out | Where-Object { $_ -match '^\s*\[EmuCore/GS\]\s*$' } | Select-Object -First 1
    if (-not $sectionSeen) {
        $out.Add('')
        $out.Add($sectionHeader)
    }
    foreach ($k in $keysMissing) {
        $out.Add("$k = $($keysToSet[$k])")
    }
}

Set-Content -LiteralPath $IniAbs -Value $out -Encoding UTF8
Write-Host "INI patched with SaveRT settings."

# Always restore the INI on the way out, even on Ctrl+C.
$cleanupNeeded = $true
function Restore-Ini {
    if ($script:cleanupNeeded) {
        Write-Host "Restoring original INI..."
        Copy-Item -LiteralPath $IniBackup -Destination $IniAbs -Force
        $script:cleanupNeeded = $false
    }
}

try {
    Write-Host "Spawning PCSX2 in batch mode for $DurationSeconds seconds..."
    $proc = Start-Process -FilePath $PcsX2Abs `
        -ArgumentList @('-batch', '-nogui', '--', $GsDumpAbs) `
        -PassThru -WindowStyle Hidden

    $deadline = [DateTime]::UtcNow.AddSeconds($DurationSeconds)
    while ([DateTime]::UtcNow -lt $deadline -and -not $proc.HasExited) {
        Start-Sleep -Milliseconds 500
    }

    if (-not $proc.HasExited) {
        Write-Host "Timer elapsed — terminating PCSX2 (PID $($proc.Id))."
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        $proc.WaitForExit(5000) | Out-Null
    }
    else {
        Write-Host "PCSX2 exited on its own with code $($proc.ExitCode)."
    }
} finally {
    Restore-Ini
}

$rtCount = (Get-ChildItem -LiteralPath $OutDirAbs -Filter '*_rt0_*.png' -ErrorAction SilentlyContinue).Count
$alphaCount = (Get-ChildItem -LiteralPath $OutDirAbs -Filter '*_alpha.png' -ErrorAction SilentlyContinue).Count
$contextCount = (Get-ChildItem -LiteralPath $OutDirAbs -Filter '*_context.txt' -ErrorAction SilentlyContinue).Count
Write-Host ""
Write-Host "Capture summary:"
Write-Host "  RT PNGs       : $rtCount"
Write-Host "  Alpha PNGs    : $alphaCount"
Write-Host "  Context files : $contextCount"
Write-Host "  Directory     : $OutDirAbs"
