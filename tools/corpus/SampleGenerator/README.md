# SampleGenerator

`SampleGenerator` builds the repository's ignored `Sample/Builds` corpus from a private media collection. It caches extracted media in a separate research tree, mirrors each configured build into the sample tree, and then runs the main application's recursive archive unpacker in-place.

## Configuration

The media root is required. Pass paths on the command line or use the corresponding environment variables:

| Command-line option | Environment variable | Purpose |
| --- | --- | --- |
| `--media-root <path>` | `NEVERSOFT_MEDIA_ROOT` | Read-only source media tree. |
| `--research-root <path>` | `NEVERSOFT_RESEARCH_ROOT` | Cached extracted builds. Defaults to a `Builds` sibling of the media root. |
| `--sample-root <path>` | `NEVERSOFT_SAMPLE_ROOT` | Generated sample tree. Defaults to `Sample/Builds` in this repository. |

Each configured build is read from `<media-root>/<configured build name>/`. Put an extracted directory tree there, or place supported disc images (`.iso`, `.gdi`, data-track `.bin`, or `.img`) directly in that directory.

Examples:

```powershell
dotnet run --project tools/corpus/SampleGenerator -- --media-root D:\NeversoftMedia
dotnet run --project tools/corpus/SampleGenerator -- --media-root D:\NeversoftMedia --build "Pro Skater 3" --repopulate
```

`--repopulate` removes the selected per-build research cache before extracting it again. Every recursive deletion is rejected unless its target is a strict descendant of the configured research or sample root; overlapping roots and reparse points are also rejected.

All mirrored, disc-extracted, and MSI-derived file destinations are canonicalized and must remain strict descendants of their configured output directory. Run the focused checks with `dotnet run --project tools/corpus/SampleGenerator -- --self-test`.

The MSI path requires Windows Installer COM support and `7z` on `PATH`. PS3 Blu-ray images also shell to `7z` (DiscUtils cannot read their UDF 2.50 metadata partition; 7z warns with exit code 2 on these images, so success is judged by comparing extracted file count against the listing). Xbox 360 full XGD dumps are delegated to the main tool's XDVDFS reader, which probes the known game-partition bases (including the redump XGD2 base 0xFD90000). Building requires the .NET 10 SDK.
