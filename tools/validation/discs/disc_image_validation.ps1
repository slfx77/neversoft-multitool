# Disc-image extraction validation sweep.
# Extracts one real dump of every supported container/filesystem combination
# from the research Media folder and reports file counts + timing.
#
# Usage: pwsh tools/validation/discs/disc_image_validation.ps1 [-Cli <path-to-NeversoftMultitool.exe>] [-Media <corpus-root>]
# NEVERSOFT_MEDIA_ROOT can supply the corpus root when -Media is omitted.
param(
    [string]$Cli = "src/NeversoftMultitool/bin/Debug/net10.0/NeversoftMultitool.exe",
    [string]$Media = $(if ($env:NEVERSOFT_MEDIA_ROOT) { $env:NEVERSOFT_MEDIA_ROOT } else { Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'NeversoftMedia' }),
    [string]$Out = "TestOutput/disc_validation"
)

$targets = @(
    @{ Name = 'thps1_psx_cue';  Path = "$Media\Tony Hawk's Pro Skater (1999-9-29, PSX - Final)\Tony Hawk's Pro Skater (USA).cue" }
    @{ Name = 'apocalypse_cue'; Path = "$Media\Apocalypse (1998-11-17, PSX - Final)\Apocalypse (USA).cue" }
    @{ Name = 'smproto_img';    Path = "$Media\Spider-Man (2000-2-18, PSX - Prototype)\PSX - Spider-Man Preview 2-18-2K.img" }
    @{ Name = 'thps2_dc_gdi';   Path = "$Media\Tony Hawk's Pro Skater 2 (2000-11-15, DC - Final)\Tony Hawk's Pro Skater 2 v1.001 (2000)(Activision)(NTSC)(US)[!].gdi" }
    @{ Name = 'thps2x_xbox';    Path = "$Media\Tony Hawk's Pro Skater 2X (2001-11-15, Xbox - Final)\Tony Hawk's Pro Skater 2x (USA).iso" }
    @{ Name = 'thaw_gc';        Path = "$Media\Tony Hawk's American Wasteland (2005-8-22, GC - Final)\THAW.iso" }
    @{ Name = 'thaw_pc_disk1';  Path = "$Media\Tony Hawk's American Wasteland (2006-2-6, PC - Final)\Disk1.iso" }
    @{ Name = 'thug2_ps2';      Path = "$Media\Tony Hawk's Underground 2 (2004-8-22, PS2 - Final)\SLUS-20965.iso" }
)

foreach ($t in $targets) {
    if (-not (Test-Path -LiteralPath $t.Path)) {
        Write-Host "SKIP $($t.Name): missing $($t.Path)"
        continue
    }

    $dest = Join-Path $Out $t.Name
    New-Item -ItemType Directory -Force $dest | Out-Null
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $result = & $Cli archive $t.Path -o $dest 2>&1 | Select-Object -Last 3
    $sw.Stop()
    $count = (Get-ChildItem $dest -Recurse -File | Measure-Object).Count
    $size = [math]::Round((Get-ChildItem $dest -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
    Write-Host ("{0,-16} {1,5} files {2,9} MB  {3,7:F1}s  | {4}" -f $t.Name, $count, $size, $sw.Elapsed.TotalSeconds, ($result -join ' / '))
}
