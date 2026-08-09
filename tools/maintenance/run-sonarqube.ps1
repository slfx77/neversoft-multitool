[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$HostUrl,

    [Parameter(Mandatory = $true)]
    [string]$ProjectKey,

    [Parameter(Mandatory = $true)]
    [string]$ProjectName,

    [string]$BranchName,

    [string]$SolutionPath = "NeversoftMultitool.slnx",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($env:SONAR_TOKEN)) {
    throw "Set the SONAR_TOKEN environment variable before running this script."
}

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

$repoRoot = Get-RepositoryRoot -StartPath $PSScriptRoot
$beginArgs = @(
    "sonarscanner", "begin",
    "/k:$ProjectKey",
    "/n:$ProjectName",
    "/d:sonar.host.url=$HostUrl",
    "/d:sonar.token=$($env:SONAR_TOKEN)"
)

if (-not [string]::IsNullOrWhiteSpace($BranchName)) {
    $beginArgs += "/d:sonar.branch.name=$BranchName"
}

$buildExitCode = 0
$beginSucceeded = $false

Push-Location $repoRoot
try {
    & dotnet @beginArgs
    $buildExitCode = $LASTEXITCODE
    if ($buildExitCode -eq 0) {
        $beginSucceeded = $true
        & dotnet build $SolutionPath -c $Configuration -v minimal
        $buildExitCode = $LASTEXITCODE
    }
}
finally {
    if ($beginSucceeded) {
        & dotnet sonarscanner end "/d:sonar.token=$($env:SONAR_TOKEN)"
        if ($buildExitCode -eq 0 -and $LASTEXITCODE -ne 0) {
            $buildExitCode = $LASTEXITCODE
        }
    }

    Pop-Location
}

exit $buildExitCode
