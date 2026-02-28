using System.CommandLine;
using System.Diagnostics;
using System.Text;
using NeversoftMultitool.Core;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

internal static class QbKeyCrossRefCommand
{
    public static Command CreateCommand()
    {
        var ddmDirArg = new Argument<string>("ddm-dir")
        {
            Description = "Path to directory containing DDM files"
        };
        var psxDirArg = new Argument<string>("psx-dir")
        {
            Description = "Path to directory containing PSX files"
        };
        var exportOption = new Option<string?>("-e", "--export")
        {
            Description = "Export merged mappings to file (name=0xHASH format, can replace QbKeyNames.txt)"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Show per-file match details"
        };
        var unmatchedOption = new Option<bool>("--show-unmatched")
        {
            Description = "Show unmatched DDM names and PSX hashes"
        };
        var scanArchivesOption = new Option<string?>("--scan-archives")
        {
            Description = "Scan archive filenames (WAD/PRE/BON/PKR/DDX) under this builds path for texture hash matches"
        };
        var scanPshOption = new Option<string?>("--scan-psh")
        {
            Description = "Scan .psh header files under this builds path for mesh part names"
        };

        var crossRefCmd = new Command("cross-ref",
            "Cross-reference DDM plaintext names against PSX hashes");
        crossRefCmd.Arguments.Add(ddmDirArg);
        crossRefCmd.Arguments.Add(psxDirArg);
        crossRefCmd.Options.Add(exportOption);
        crossRefCmd.Options.Add(verboseOption);
        crossRefCmd.Options.Add(unmatchedOption);
        crossRefCmd.Options.Add(scanArchivesOption);
        crossRefCmd.Options.Add(scanPshOption);

        crossRefCmd.SetAction((parseResult, cancellationToken) =>
        {
            var ddmDir = parseResult.GetValue(ddmDirArg)!;
            var psxDir = parseResult.GetValue(psxDirArg)!;
            var export = parseResult.GetValue(exportOption);
            var verbose = parseResult.GetValue(verboseOption);
            var showUnmatched = parseResult.GetValue(unmatchedOption);
            var scanArchives = parseResult.GetValue(scanArchivesOption);
            var scanPsh = parseResult.GetValue(scanPshOption);

            return Task.FromResult(Execute(ddmDir, psxDir, export, verbose, showUnmatched,
                scanArchives, scanPsh));
        });

        return crossRefCmd;
    }

    private static int Execute(string ddmDir, string psxDir, string? export,
        bool verbose, bool showUnmatched, string? scanArchives, string? scanPsh)
    {
        if (!Directory.Exists(ddmDir))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] DDM directory not found: {ddmDir}");
            return 1;
        }

        if (!Directory.Exists(psxDir))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] PSX directory not found: {psxDir}");
            return 1;
        }

        var stopwatch = Stopwatch.StartNew();
        var result = QbKeyCrossRef.Run(ddmDir, psxDir);
        stopwatch.Stop();

        DisplaySummaryHeader(result);
        if (verbose) DisplayPerFileDetails(result);
        if (result.AllDiscoveredMappings.Count > 0) DisplayDiscoveredMappings(result);
        if (showUnmatched) DisplayUnmatched(result);
        DisplayCrossRefTotals(result, stopwatch.Elapsed);

        // Archive scan
        ArchiveScanResult? archiveScan = null;
        if (!string.IsNullOrEmpty(scanArchives))
        {
            if (!Directory.Exists(scanArchives))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Builds directory not found: {scanArchives}");
                return 1;
            }

            archiveScan = RunArchiveScan(scanArchives, psxDir);
        }

        // PSH scan
        PshScanResult? pshScan = null;
        if (!string.IsNullOrEmpty(scanPsh))
        {
            if (!Directory.Exists(scanPsh))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] PSH builds directory not found: {scanPsh}");
                return 1;
            }

            pshScan = RunPshScan(scanPsh, psxDir);
        }

        // Export (includes archive + PSH scan matches if available)
        if (!string.IsNullOrEmpty(export))
            ExportMappings(export, result.AllDiscoveredMappings,
                archiveScan?.AllMatches, pshScan?.Matches);

        return 0;
    }

    private static void DisplaySummaryHeader(CrossRefResult result)
    {
        AnsiConsole.MarkupLine($"DDM files: [green]{result.TotalDdmFiles}[/], " +
                               $"PSX files: [green]{result.TotalPsxFiles}[/], " +
                               $"Matched pairs: [green]{result.MatchedFilePairs}[/]");
        AnsiConsole.WriteLine();
    }

    private static void DisplayPerFileDetails(CrossRefResult result)
    {
        var fileTable = new Table();
        fileTable.AddColumn("DDM File");
        fileTable.AddColumn("PSX File");
        fileTable.AddColumn(new TableColumn("Matches").RightAligned());
        fileTable.AddColumn(new TableColumn("DDM Names").RightAligned());
        fileTable.AddColumn(new TableColumn("PSX Hashes").RightAligned());
        fileTable.AddColumn(new TableColumn("Rate").RightAligned());

        foreach (var fr in result.FileResults)
        {
            var rate = fr.PsxHashCount > 0
                ? $"{(double)fr.Matches.Count / fr.PsxHashCount:P0}"
                : "-";
            var matchColor = fr.Matches.Count > 0 ? "green" : "dim";
            fileTable.AddRow(
                fr.DdmFile,
                fr.PsxFile,
                $"[{matchColor}]{fr.Matches.Count}[/]",
                fr.DdmNameCount.ToString(),
                fr.PsxHashCount.ToString(),
                rate);
        }

        AnsiConsole.Write(fileTable);
        AnsiConsole.WriteLine();
    }

    private static void DisplayDiscoveredMappings(CrossRefResult result)
    {
        AnsiConsole.MarkupLine($"[bold]Discovered Mappings ({result.AllDiscoveredMappings.Count} unique, " +
                               $"{result.NewDiscoveries} new):[/]");

        var mappingTable = new Table();
        mappingTable.AddColumn("Name");
        mappingTable.AddColumn("Hash");
        mappingTable.AddColumn("Status");

        foreach (var (name, hash) in result.AllDiscoveredMappings.OrderBy(kv => kv.Key))
        {
            var existing = QbKey.TryResolve(hash);
            var status = existing != null ? "[dim]known[/]" : "[green]NEW[/]";
            mappingTable.AddRow(name, $"0x{hash:X8}", status);
        }

        AnsiConsole.Write(mappingTable);
        AnsiConsole.WriteLine();
    }

    private static void DisplayUnmatched(CrossRefResult result)
    {
        var allUnmatchedNames = result.FileResults
            .SelectMany(fr => fr.UnmatchedDdmNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (allUnmatchedNames.Count > 0)
        {
            AnsiConsole.MarkupLine($"[bold]Unmatched DDM Names ({allUnmatchedNames.Count}):[/]");
            var nameTable = new Table();
            nameTable.AddColumn("Name");
            nameTable.AddColumn("QBKey Hash");

            foreach (var name in allUnmatchedNames)
                nameTable.AddRow(name, $"0x{QbKey.Hash(name):X8}");

            AnsiConsole.Write(nameTable);
            AnsiConsole.WriteLine();
        }

        var allUnmatchedHashes = result.FileResults
            .SelectMany(fr => fr.UnmatchedPsxHashes)
            .Distinct()
            .OrderBy(h => h)
            .ToList();

        if (allUnmatchedHashes.Count > 0)
        {
            AnsiConsole.MarkupLine($"[bold]Unmatched PSX Hashes ({allUnmatchedHashes.Count}):[/]");
            var hashTable = new Table();
            hashTable.AddColumn("Hash");
            hashTable.AddColumn("Known Name");

            foreach (var hash in allUnmatchedHashes)
            {
                var known = QbKey.TryResolve(hash) ?? "-";
                hashTable.AddRow($"0x{hash:X8}", known);
            }

            AnsiConsole.Write(hashTable);
            AnsiConsole.WriteLine();
        }
    }

    private static void DisplayCrossRefTotals(CrossRefResult result, TimeSpan elapsed)
    {
        var meshPct = result.TotalMeshHashes > 0
            ? $" ({(double)result.TotalMeshMatches / result.TotalMeshHashes:P1})"
            : "";
        var texPct = result.TotalTextureHashes > 0
            ? $" ({(double)result.TotalTextureMatches / result.TotalTextureHashes:P1})"
            : "";
        AnsiConsole.MarkupLine(
            $"Total matches: [green]{result.TotalMatches}[/], " +
            $"Unique mappings: [green]{result.AllDiscoveredMappings.Count}[/], " +
            $"New discoveries: [green]{result.NewDiscoveries}[/] " +
            $"in {elapsed.TotalSeconds:F2}s");
        AnsiConsole.MarkupLine(
            $"  Mesh hashes:    [green]{result.TotalMeshMatches}[/] / {result.TotalMeshHashes} matched{meshPct}");
        AnsiConsole.MarkupLine(
            $"  Texture hashes: [green]{result.TotalTextureMatches}[/] / {result.TotalTextureHashes} matched{texPct}");
    }

    private static ArchiveScanResult RunArchiveScan(string buildsPath, string psxDir)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Archive Filename Scan[/]");

        var stopwatch = Stopwatch.StartNew();
        var scan = QbKeyCrossRef.ScanArchiveNames(buildsPath, psxDir);
        stopwatch.Stop();

        AnsiConsole.MarkupLine(
            $"Scanned [green]{scan.ArchivesScanned}[/] archives " +
            $"({scan.ArchiveErrors} errors), " +
            $"[green]{scan.TotalCandidateNames}[/] unique candidate names " +
            $"in {stopwatch.Elapsed.TotalSeconds:F2}s");

        var texPct = scan.TotalTextureHashes > 0
            ? $" ({(double)scan.TextureMatches.Count / scan.TotalTextureHashes:P1})"
            : "";
        AnsiConsole.MarkupLine(
            $"  Texture matches: [green]{scan.TextureMatches.Count}[/] / {scan.TotalTextureHashes}{texPct}");
        AnsiConsole.MarkupLine(
            $"  Mesh matches:    [green]{scan.MeshMatches.Count}[/] / {scan.TotalMeshHashes}");
        AnsiConsole.MarkupLine(
            $"  New discoveries: [green]{scan.NewDiscoveries}[/]");

        DisplayArchiveMatchTable("Texture", scan.TextureMatches);
        DisplayArchiveMatchTable("Mesh", scan.MeshMatches);

        return scan;
    }

    private static PshScanResult RunPshScan(string buildsPath, string psxDir)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]PSH Part Name Scan[/]");

        var stopwatch = Stopwatch.StartNew();
        var scan = PshScanner.ScanPshNames(buildsPath, psxDir);
        stopwatch.Stop();

        AnsiConsole.MarkupLine(
            $"Scanned [green]{scan.TotalPshFiles}[/] .psh files, " +
            $"[green]{scan.TotalCandidateNames}[/] unique candidate names " +
            $"in {stopwatch.Elapsed.TotalSeconds:F2}s");

        var meshPct = scan.TotalMeshHashes > 0
            ? $" ({(double)scan.Matches.Count / scan.TotalMeshHashes:P1})"
            : "";
        AnsiConsole.MarkupLine(
            $"  Mesh matches:    [green]{scan.Matches.Count}[/] / {scan.TotalMeshHashes}{meshPct}");
        AnsiConsole.MarkupLine(
            $"  New discoveries: [green]{scan.NewDiscoveries}[/]");

        if (scan.Matches.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[bold]PSH Mesh Matches ({scan.Matches.Count}):[/]");

            var matchTable = new Table();
            matchTable.AddColumn("Name");
            matchTable.AddColumn("Hash");
            matchTable.AddColumn("Status");

            foreach (var m in scan.Matches.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            {
                var known = QbKey.TryResolve(m.Hash);
                var status = known != null ? "[dim]known[/]" : "[green]NEW[/]";
                matchTable.AddRow(m.Name, $"0x{m.Hash:X8}", status);
            }

            AnsiConsole.Write(matchTable);
        }

        return scan;
    }

    private static void DisplayArchiveMatchTable(string pool, List<QbKeyMapping> matches)
    {
        if (matches.Count == 0) return;

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]Archive {pool} Matches ({matches.Count}):[/]");

        var matchTable = new Table();
        matchTable.AddColumn("Name");
        matchTable.AddColumn("Hash");
        matchTable.AddColumn("Status");

        foreach (var m in matches.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            var known = QbKey.TryResolve(m.Hash);
            var status = known != null ? "[dim]known[/]" : "[green]NEW[/]";
            matchTable.AddRow(m.Name, $"0x{m.Hash:X8}", status);
        }

        AnsiConsole.Write(matchTable);
    }

    internal static void ExportMappings(string path, Dictionary<string, uint> crossRefMappings,
        List<QbKeyMapping>? archiveMatches, List<QbKeyMapping>? pshMatches = null)
    {
        // Merge all sources with existing known names
        var merged = new Dictionary<uint, string>(QbKey.GetAllKnownMappings());
        foreach (var (name, hash) in crossRefMappings)
            merged.TryAdd(hash, name);

        if (archiveMatches != null)
        {
            foreach (var m in archiveMatches)
                merged.TryAdd(m.Hash, m.Name.ToLowerInvariant());
        }

        if (pshMatches != null)
        {
            foreach (var m in pshMatches)
                merged.TryAdd(m.Hash, m.Name);
        }

        var sorted = merged.OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase).ToList();
        var sb = new StringBuilder();

        foreach (var (hash, name) in sorted)
            sb.AppendLine($"{name}=0x{hash:X8}");

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(path, sb.ToString());
        AnsiConsole.MarkupLine($"Exported {sorted.Count} mappings to [green]{path}[/]");
    }
}
