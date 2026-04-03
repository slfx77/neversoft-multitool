# Code Quality Workflow

This repo keeps code cleanup local-first.

## SonarQube

Use the local wrapper in [tools/run-sonarqube.ps1](/c:/Users/mmc99/source/repos/NeversoftMultitool/tools/run-sonarqube.ps1).

Prerequisites:

- `dotnet-sonarscanner` is installed and available on `PATH`
- `SONAR_TOKEN` is set in the environment

Example:

```powershell
$env:SONAR_TOKEN = "..."
.\tools\run-sonarqube.ps1 `
  -HostUrl "https://sonarqube.example.com" `
  -ProjectKey "NeversoftMultitool" `
  -ProjectName "NeversoftMultitool"
```

Optional inputs:

- `-BranchName`
- `-SolutionPath`
- `-Configuration`

## Warning-Clean Target

The cleanup target for `src` and `tests` is a warning-clean `dotnet build NeversoftMultitool.slnx -v minimal`.

That includes:

- Roslyn/compiler warnings
- SonarAnalyzer warnings
- XML documentation mismatches
- xUnit analyzer warnings

## Repo Rules

- Partial classes are limited to XAML code-behind and generated-regex bridge types.
- Tracked C# files under `src` and `tests` should stay near the soft `<=500` line target.
- Any intentional exceptions to the soft size target must stay explicit in `RepoPolicyTests`.
- GUI code-behind should remain UI wiring only; non-UI workflows belong in helpers/controllers/services.
