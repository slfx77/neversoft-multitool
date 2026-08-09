# THAW zone-texture diagnostics

This slim .NET CLI retains four reusable triage commands for zone-texture
oracle failures: `archive-stex-diagnostics`, `archive-img-alpha-diagnostics`,
`decode-provenance`, and `content-search`.

It requires the .NET 10 SDK and restores `System.CommandLine` and ImageSharp;
it also references the main application project. Build and inspect the command
surface from the repository root:

```powershell
dotnet build tools/validation/thaw-zone-texture/ThawZoneTexAnalyzer.csproj
dotnet run --project tools/validation/thaw-zone-texture/ThawZoneTexAnalyzer.csproj -- --help
```

Commands require explicit shipped-archive, checksum, or reference-image inputs.
Their generated PNG and CSV diagnostics default to directories under
`TestOutput/`.
