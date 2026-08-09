[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$SoftFileLineLimit = 500
$LargeFileBaseline = @{
    "src/NeversoftMultitool/App/Tabs/HashReviewerTab.xaml.cs"      = 508
    "src/NeversoftMultitool/App/Tabs/MeshConverterTabFileConverter.cs" = 506
    "src/NeversoftMultitool/App/Tabs/MeshConverterTabFileScanner.cs"   = 501
}
$AllowedMeshRootFiles = @(
    "src/NeversoftMultitool/Core/Formats/Mesh/GltfNormalSmoother.cs",
    "src/NeversoftMultitool/Core/Formats/Mesh/GltfTextureHelper.cs"
)

function Get-RepositoryRoot {
    param([string]$StartPath)

    $current = [System.IO.DirectoryInfo]::new($StartPath)
    while ($null -ne $current) {
        if (Test-Path -LiteralPath (Join-Path $current.FullName "Directory.Build.props")) {
            return $current.FullName
        }

        $current = $current.Parent
    }

    throw "Could not locate repository root from '$StartPath'."
}

function Get-RepoRelativePath {
    param(
        [string]$RepositoryRoot,
        [string]$Path
    )

    return [System.IO.Path]::GetRelativePath($RepositoryRoot, $Path).Replace('\', '/')
}

function Test-IsTrackedCSharpFile {
    param([string]$Path)

    return -not $Path.Contains([System.IO.Path]::DirectorySeparatorChar + "obj" + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase) `
        -and -not $Path.Contains([System.IO.Path]::DirectorySeparatorChar + "bin" + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase) `
        -and -not $Path.EndsWith(".g.cs", [System.StringComparison]::OrdinalIgnoreCase) `
        -and -not $Path.EndsWith(".designer.cs", [System.StringComparison]::OrdinalIgnoreCase) `
        -and -not $Path.EndsWith(".generated.cs", [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-GitTrackedCSharpFiles {
    param([string]$RepositoryRoot)

    try {
        $output = & git -c "safe.directory=*" ls-files -- src tests 2>$null
        if ($LASTEXITCODE -ne 0) {
            return $null
        }

        return @(
            $output |
            Where-Object { $_ -and $_.EndsWith(".cs", [System.StringComparison]::OrdinalIgnoreCase) } |
            ForEach-Object { Join-Path $RepositoryRoot ($_.Replace('/', [System.IO.Path]::DirectorySeparatorChar)) }
        )
    }
    catch {
        return $null
    }
}

function Get-FallbackCSharpFiles {
    param([string]$RepositoryRoot)

    $results = New-Object System.Collections.Generic.List[string]
    foreach ($relativeRoot in @("src", "tests")) {
        $absoluteRoot = Join-Path $RepositoryRoot $relativeRoot
        if (-not (Test-Path -LiteralPath $absoluteRoot)) {
            continue
        }

        foreach ($path in Get-ChildItem -Path $absoluteRoot -Recurse -Filter *.cs -File | Select-Object -ExpandProperty FullName) {
            $results.Add($path)
        }
    }

    return $results
}

function Get-TrackedCSharpFiles {
    param([string]$RepositoryRoot)

    $paths = Get-GitTrackedCSharpFiles -RepositoryRoot $RepositoryRoot
    if ($null -eq $paths) {
        $paths = Get-FallbackCSharpFiles -RepositoryRoot $RepositoryRoot
    }

    return @(
        $paths |
        Where-Object { Test-IsTrackedCSharpFile $_ } |
        Where-Object { Test-Path -LiteralPath $_ }
    )
}

function Get-LineCount {
    param([string]$Path)

    $count = 0
    foreach ($nullValue in [System.IO.File]::ReadLines($Path)) {
        $count++
    }

    return $count
}

function Test-ContainsPartialClassDeclaration {
    param([string]$Path)

    $content = [System.IO.File]::ReadAllText($Path)
    return [System.Text.RegularExpressions.Regex]::IsMatch(
        $content,
        "\bpartial\s+class\b",
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
}

function Test-IsAllowedPartialClassUsage {
    param(
        [string]$RepositoryRoot,
        [string]$RelativePath
    )

    if ($RelativePath.EndsWith(".xaml.cs", [System.StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    $fullPath = Join-Path $RepositoryRoot ($RelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
    $content = [System.IO.File]::ReadAllText($fullPath)
    return $content.Contains("[GeneratedRegex(", [System.StringComparison]::Ordinal) `
        -or $content.Contains(": JsonSerializerContext", [System.StringComparison]::Ordinal)
}

function Write-ViolationSection {
    param(
        [string]$Title,
        [string[]]$Lines
    )

    Write-Host $Title
    foreach ($line in $Lines) {
        Write-Host ("  - " + $line)
    }
    Write-Host
}

$repositoryRoot = Get-RepositoryRoot -StartPath $PSScriptRoot
$trackedFiles = Get-TrackedCSharpFiles -RepositoryRoot $repositoryRoot

$oversizedFiles = @(
    foreach ($path in $trackedFiles) {
        $relativePath = Get-RepoRelativePath -RepositoryRoot $repositoryRoot -Path $path
        $lineCount = Get-LineCount -Path $path
        if ($lineCount -gt $SoftFileLineLimit) {
            [PSCustomObject]@{
                RelativePath = $relativePath
                LineCount    = $lineCount
            }
        }
    }
) | Sort-Object @{ Expression = "LineCount"; Descending = $true }, @{ Expression = "RelativePath"; Descending = $false }

$lineViolations = @(
    $oversizedFiles |
    Where-Object {
        -not $LargeFileBaseline.ContainsKey($_.RelativePath) -or $_.LineCount -gt $LargeFileBaseline[$_.RelativePath]
    } |
    ForEach-Object { "$($_.RelativePath) ($($_.LineCount) lines)" }
)

$partialViolations = @(
    $trackedFiles |
    Where-Object { Test-ContainsPartialClassDeclaration $_ } |
    ForEach-Object { Get-RepoRelativePath -RepositoryRoot $repositoryRoot -Path $_ } |
    Where-Object { -not (Test-IsAllowedPartialClassUsage -RepositoryRoot $repositoryRoot -RelativePath $_) } |
    Sort-Object
)

$legacyPsxViolations = @(
    $trackedFiles |
    ForEach-Object { Get-RepoRelativePath -RepositoryRoot $repositoryRoot -Path $_ } |
    Where-Object {
        $_.StartsWith("src/NeversoftMultitool/Core/Formats/Psx/", [System.StringComparison]::OrdinalIgnoreCase) -or
        $_.StartsWith("tests/NeversoftMultitool.Tests/Core/Formats/Psx/", [System.StringComparison]::OrdinalIgnoreCase)
    } |
    Sort-Object
)

$legacyPs2SceneViolations = @(
    $trackedFiles |
    ForEach-Object { Get-RepoRelativePath -RepositoryRoot $repositoryRoot -Path $_ } |
    Where-Object {
        $_.StartsWith("src/NeversoftMultitool/Core/Formats/Ps2Scene/", [System.StringComparison]::OrdinalIgnoreCase) -or
        $_.StartsWith("tests/NeversoftMultitool.Tests/Core/Formats/Ps2Scene/", [System.StringComparison]::OrdinalIgnoreCase)
    } |
    Sort-Object
)

$legacyXbxSceneViolations = @(
    $trackedFiles |
    ForEach-Object { Get-RepoRelativePath -RepositoryRoot $repositoryRoot -Path $_ } |
    Where-Object {
        $_.StartsWith("src/NeversoftMultitool/Core/Formats/XbxScene/", [System.StringComparison]::OrdinalIgnoreCase) -or
        $_.StartsWith("tests/NeversoftMultitool.Tests/Core/Formats/XbxScene/", [System.StringComparison]::OrdinalIgnoreCase)
    } |
    Sort-Object
)

$meshRootViolations = @(
    $trackedFiles |
    ForEach-Object { Get-RepoRelativePath -RepositoryRoot $repositoryRoot -Path $_ } |
    Where-Object { $_.StartsWith("src/NeversoftMultitool/Core/Formats/Mesh/", [System.StringComparison]::OrdinalIgnoreCase) } |
    Where-Object {
        $relativeToMeshRoot = $_.Substring("src/NeversoftMultitool/Core/Formats/Mesh/".Length)
        -not $relativeToMeshRoot.Contains('/', [System.StringComparison]::Ordinal)
    } |
    Where-Object { $AllowedMeshRootFiles -notcontains $_ } |
    Sort-Object
)

$hasViolations = $false

if ($lineViolations.Count -gt 0) {
    $hasViolations = $true
    Write-Host "Tracked C# files under src/ and tests/ should stay at or below $SoftFileLineLimit lines unless explicitly tracked in the baseline."
    Write-Host "Split new oversized files instead of extending the baseline."
    Write-Host
    Write-ViolationSection -Title "Line limit violations:" -Lines $lineViolations
}

if ($partialViolations.Count -gt 0) {
    $hasViolations = $true
    Write-Host "Partial class usage is reserved for XAML code-behind, GeneratedRegex bridge types, and JsonSerializerContext source-generation bridges."
    Write-Host
    Write-ViolationSection -Title "Partial usage violations:" -Lines $partialViolations
}

if ($legacyPsxViolations.Count -gt 0) {
    $hasViolations = $true
    Write-ViolationSection -Title "Legacy Core/Formats/Psx bucket must stay empty:" -Lines $legacyPsxViolations
}

if ($legacyPs2SceneViolations.Count -gt 0) {
    $hasViolations = $true
    Write-ViolationSection -Title "Legacy Core/Formats/Ps2Scene bucket must stay empty:" -Lines $legacyPs2SceneViolations
}

if ($legacyXbxSceneViolations.Count -gt 0) {
    $hasViolations = $true
    Write-ViolationSection -Title "Legacy Core/Formats/XbxScene bucket must stay empty:" -Lines $legacyXbxSceneViolations
}

if ($meshRootViolations.Count -gt 0) {
    $hasViolations = $true
    Write-Host "Core/Formats/Mesh root is reserved for shared helpers only."
    Write-Host "Allowed root files:"
    foreach ($allowedFile in $AllowedMeshRootFiles) {
        Write-Host ("  - " + $allowedFile)
    }
    Write-Host
    Write-ViolationSection -Title "Mesh root violations:" -Lines $meshRootViolations
}

if ($hasViolations) {
    exit 1
}

Write-Host "Repo structure checks passed."
if ($oversizedFiles.Count -gt 0) {
    Write-Host
    Write-Host "Tracked soft-limit exceptions:"
    foreach ($file in $oversizedFiles) {
        Write-Host ("  - " + $file.RelativePath + " (" + $file.LineCount + " lines)")
    }
}
