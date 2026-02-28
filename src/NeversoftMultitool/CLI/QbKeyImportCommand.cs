using System.CommandLine;
using NeversoftMultitool.Core;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

internal static class QbKeyImportCommand
{
    public static Command CreateCommand()
    {
        var namesFileArg = new Argument<string>("names-file")
        {
            Description = "Text file with candidate names (one per line)"
        };
        var psxDirOption = new Option<string?>("--psx-dir")
        {
            Description = "PSX directory to check matches against"
        };
        var exportOption = new Option<string?>("-e", "--export")
        {
            Description = "Export merged mappings to file (name=0xHASH format)"
        };

        var importCmd = new Command("import",
            "Import candidate names from C pipeline output and check against PSX hashes");
        importCmd.Arguments.Add(namesFileArg);
        importCmd.Options.Add(psxDirOption);
        importCmd.Options.Add(exportOption);

        importCmd.SetAction((parseResult, cancellationToken) =>
        {
            var namesFile = parseResult.GetValue(namesFileArg)!;
            var psxDir = parseResult.GetValue(psxDirOption);
            var export = parseResult.GetValue(exportOption);

            return Task.FromResult(ExecuteImport(namesFile, psxDir, export));
        });

        return importCmd;
    }

    private static int ExecuteImport(string namesFile, string? psxDir, string? export)
    {
        if (!File.Exists(namesFile))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Names file not found: {namesFile}");
            return 1;
        }

        // Read candidate names
        var names = File.ReadAllLines(namesFile)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        AnsiConsole.MarkupLine($"Loaded [green]{names.Count}[/] candidate names from {Path.GetFileName(namesFile)}");

        // Hash all names and check against existing dictionary
        var newMappings = new Dictionary<uint, string>();
        var knownCount = 0;
        foreach (var name in names)
        {
            var hash = QbKey.Hash(name);
            if (QbKey.TryResolve(hash) != null)
                knownCount++;
            else
                newMappings.TryAdd(hash, name);
        }

        AnsiConsole.MarkupLine(
            $"  Already known: [dim]{knownCount}[/], New hashes: [green]{newMappings.Count}[/]");

        // Check against PSX hashes if directory provided
        if (!string.IsNullOrEmpty(psxDir))
        {
            if (!Directory.Exists(psxDir))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] PSX directory not found: {psxDir}");
                return 1;
            }

            DisplayPsxMatches(names, psxDir);
        }

        // Export merged dictionary
        if (!string.IsNullOrEmpty(export))
        {
            var allMappings = new Dictionary<string, uint>();
            foreach (var name in names)
                allMappings.TryAdd(name, QbKey.Hash(name));
            QbKeyCrossRefCommand.ExportMappings(export, allMappings, null);
        }

        return 0;
    }

    private static void DisplayPsxMatches(HashSet<string> names, string psxDir)
    {
        var (textureHashes, meshHashes) = QbKeyCrossRef.CollectAllPsxHashes(psxDir);

        var textureMatches = new List<(string Name, uint Hash)>();
        var meshMatches = new List<(string Name, uint Hash)>();

        foreach (var name in names)
        {
            var hash = QbKey.Hash(name);
            if (textureHashes.Contains(hash))
                textureMatches.Add((name, hash));
            if (meshHashes.Contains(hash))
                meshMatches.Add((name, hash));
        }

        AnsiConsole.MarkupLine(
            $"  PSX texture matches: [green]{textureMatches.Count}[/] / {textureHashes.Count}");
        AnsiConsole.MarkupLine(
            $"  PSX mesh matches:    [green]{meshMatches.Count}[/] / {meshHashes.Count}");

        DisplayImportMatchTable("Texture", textureMatches);
        DisplayImportMatchTable("Mesh", meshMatches);
    }

    private static void DisplayImportMatchTable(string pool, List<(string Name, uint Hash)> matches)
    {
        if (matches.Count == 0) return;

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]{pool} Matches ({matches.Count}):[/]");

        var table = new Table();
        table.AddColumn("Name");
        table.AddColumn("Hash");
        table.AddColumn("Status");

        foreach (var (name, hash) in matches.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            var known = QbKey.TryResolve(hash);
            var status = known != null ? "[dim]known[/]" : "[green]NEW[/]";
            table.AddRow(name, $"0x{hash:X8}", status);
        }

        AnsiConsole.Write(table);
    }
}
