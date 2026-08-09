<#
.SYNOPSIS
  Regenerate the committed GS-oracle goldens (tests/NeversoftMultitool.Tests/
  GoldenFiles/GsOracle) from the local PCSX2 .gs captures.

  Runs the gsdump audit with the zone-texture catalog over every capture in
  the snaps dir, then copies each {stem}.gsoracle.json / {stem}.texoracle.json
  into the goldens dir renamed to its 6-digit tag, and writes captures.json
  (tag -> source dump + tex source) so the adjudication tests can report which
  capture a regression came from.

  The tex source must be the extracted z_bh worldzone pak. To re-extract it,
  use the pak inside the THAW PS2 build's DATAP.WAD
  (pak/worldzones/z_bh.pak.ps2); the archive CLI or ArchiveFs nested opens
  produce it.

  Set PCSX2_SNAPS_DIR or pass -SnapDir for the capture directory. Pass
  -TexSource or set NEVERSOFT_THAW_ZONE_TEX to the extracted pak path.
#>
[CmdletBinding()]
param(
    [string] $SnapDir = $(if ($env:PCSX2_SNAPS_DIR) { $env:PCSX2_SNAPS_DIR } else { Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'PCSX2\snaps' }),
    [string] $TexSource = $env:NEVERSOFT_THAW_ZONE_TEX,
    [string] $WorkDir = 'TestOutput\gsoracle_golden_regen'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
$cli = Join-Path $repoRoot 'src\NeversoftMultitool\bin\Debug\net10.0\NeversoftMultitool.dll'
$goldenDir = Join-Path $repoRoot 'tests\NeversoftMultitool.Tests\GoldenFiles\GsOracle'

if (-not (Test-Path $cli)) { throw "Build the CLI first: dotnet build src/NeversoftMultitool/NeversoftMultitool.csproj" }
if ([string]::IsNullOrWhiteSpace($TexSource)) { throw "Pass -TexSource or set NEVERSOFT_THAW_ZONE_TEX to the extracted z_bh.pak.ps2 path" }
if (-not (Test-Path -LiteralPath $TexSource)) { throw "Tex source not found: $TexSource (extract z_bh.pak.ps2 from DATAP.WAD)" }

New-Item -ItemType Directory -Force $WorkDir | Out-Null
New-Item -ItemType Directory -Force $goldenDir | Out-Null

& dotnet $cli gsdump $SnapDir -o $WorkDir --tex $TexSource --json-only

$manifest = [ordered]@{}
Get-ChildItem -LiteralPath $WorkDir -Filter '*.gsoracle.json' | ForEach-Object {
    if ($_.Name -notmatch '_(\d{14})\.gsoracle\.json$') { return }
    $tag = $matches[1].Substring($matches[1].Length - 6)
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $goldenDir "$tag.gsoracle.json") -Force
    $texOracle = $_.FullName -replace '\.gsoracle\.json$', '.texoracle.json'
    if (Test-Path -LiteralPath $texOracle) {
        Copy-Item -LiteralPath $texOracle -Destination (Join-Path $goldenDir "$tag.texoracle.json") -Force
    }
    $manifest[$tag] = [ordered]@{
        dump      = ($_.Name -replace '\.gsoracle\.json$', '.gs')
        texSource = (Split-Path $TexSource -Leaf)
    }
}

$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $goldenDir 'captures.json') -Encoding UTF8
Write-Host "GOLDENS REGENERATED: $($manifest.Count) captures -> $goldenDir"
