using System.CommandLine;
using System.Numerics;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Ddm;
using NeversoftMultitool.Core.Formats.Mesh.Lit;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.QbKey;
using Spectre.Console;

namespace DdmAnalyzer.Commands;

public static class PlacementCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a level directory (e.g. .../Levels/skjam) or parent Levels/ directory with --all"
        };
        var allOption = new Option<bool>("--all")
        {
            Description = "Process all level subdirectories under the given parent directory"
        };

        var command = new Command("placement", "Analyze DDM/PSX level placement: hash matching, position outliers, file inventory");
        command.Arguments.Add(inputArgument);
        command.Options.Add(allOption);

        command.SetAction((parseResult, _) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var all = parseResult.GetValue(allOption);

            if (all)
                return Task.FromResult(AnalyzeAll(input));

            return Task.FromResult(AnalyzeLevel(input));
        });

        return command;
    }

    private static int AnalyzeAll(string parentDir)
    {
        if (!Directory.Exists(parentDir))
        {
            AnsiConsole.MarkupLine($"[red]Directory not found:[/] {parentDir}");
            return 1;
        }

        var levelDirs = Directory.GetDirectories(parentDir)
            .Where(d => Directory.GetFiles(d, "*.ddm",
                new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).Length > 0)
            .OrderBy(d => Path.GetFileName(d))
            .ToList();

        if (levelDirs.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No subdirectories with .ddm files found.[/]");
            return 0;
        }

        // Summary table across all levels
        var summaryTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Batch Placement Summary[/]")
            .AddColumn("Level")
            .AddColumn("DDM Objs", c => c.RightAligned())
            .AddColumn("PSX Objs", c => c.RightAligned())
            .AddColumn("Matched", c => c.RightAligned())
            .AddColumn("Unmatched", c => c.RightAligned())
            .AddColumn("Obj DDM", c => c.RightAligned())
            .AddColumn("Obj PSX", c => c.RightAligned())
            .AddColumn("Obj Match", c => c.RightAligned())
            .AddColumn("Lights", c => c.RightAligned())
            .AddColumn("Outliers");

        foreach (var dir in levelDirs)
        {
            var result = AnalyzeLevelData(dir);
            if (result == null) continue;

            var outlierCount = result.LevelOutliers.Count + result.ObjectsOutliers.Count;
            var outlierStr = outlierCount > 0
                ? $"[yellow]{outlierCount}[/]"
                : "[dim]-[/]";

            summaryTable.AddRow(
                result.LevelName,
                result.LevelDdmCount.ToString(),
                result.LevelPsxCount.ToString(),
                result.LevelMatched.ToString(),
                result.LevelUnmatched.ToString(),
                result.ObjectsDdmCount.ToString(),
                result.ObjectsPsxCount.ToString(),
                result.ObjectsMatched.ToString(),
                result.LightCount.ToString(),
                outlierStr);
        }

        AnsiConsole.Write(summaryTable);
        return 0;
    }

    private static int AnalyzeLevel(string levelDir)
    {
        if (!Directory.Exists(levelDir))
        {
            AnsiConsole.MarkupLine($"[red]Directory not found:[/] {levelDir}");
            return 1;
        }

        var result = AnalyzeLevelData(levelDir);
        if (result == null)
        {
            AnsiConsole.MarkupLine("[yellow]No DDM files found in directory.[/]");
            return 0;
        }

        PrintDetailedReport(result);
        return 0;
    }

    private static LevelAnalysis? AnalyzeLevelData(string levelDir)
    {
        var ddmFiles = Directory.GetFiles(levelDir, "*.ddm",
            new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive });
        if (ddmFiles.Length == 0) return null;

        // Identify the level DDM (not _o)
        var levelDdmPath = ddmFiles
            .FirstOrDefault(f => !Path.GetFileNameWithoutExtension(f)
                .EndsWith("_o", StringComparison.OrdinalIgnoreCase));
        if (levelDdmPath == null) return null;

        var levelName = Path.GetFileNameWithoutExtension(levelDdmPath);
        var result = new LevelAnalysis
        {
            LevelName = levelName,
            LevelDir = levelDir,
        };

        // Parse level DDM
        var levelDdm = DdmFile.Parse(levelDdmPath);
        result.LevelDdmCount = levelDdm.Objects.Count;

        // Parse objects DDM
        var objectsDdmPath = FindCompanionFile(levelDir, levelName + "_o", ".ddm");
        DdmFile? objectsDdm = null;
        if (objectsDdmPath != null)
        {
            objectsDdm = DdmFile.Parse(objectsDdmPath);
            result.ObjectsDdmCount = objectsDdm.Objects.Count;
        }

        // Parse level PSX
        var levelPsxPath = FindCompanionFile(levelDir, levelName, ".psx");
        PsxLayoutFile? levelPsx = null;
        if (levelPsxPath != null)
        {
            levelPsx = PsxLayoutFile.Parse(levelPsxPath);
            if (levelPsx != null)
            {
                result.LevelPsxCount = levelPsx.Objects.Count;
                result.LevelMeshHashes = levelPsx.MeshNameHashes.Length;
            }
        }

        // Parse objects PSX
        var objectsPsxPath = FindCompanionFile(levelDir, levelName + "_o", ".psx");
        PsxLayoutFile? objectsPsx = null;
        if (objectsPsxPath != null)
        {
            objectsPsx = PsxLayoutFile.Parse(objectsPsxPath);
            if (objectsPsx != null)
            {
                result.ObjectsPsxCount = objectsPsx.Objects.Count;
                result.ObjectsMeshHashes = objectsPsx.MeshNameHashes.Length;
            }
        }

        // Check .lit file
        var litPath = FindCompanionFile(levelDir, levelName, ".lit");
        if (litPath != null)
        {
            result.HasLitFile = true;
            try
            {
                var lights = LitFile.Parse(litPath);
                result.LightCount = lights.Count;
            }
            catch { /* parse failure */ }
        }

        // Count DDX textures
        var ddxPath = FindCompanionFile(levelDir, levelName, ".ddx");
        var ddxObjPath = FindCompanionFile(levelDir, levelName + "_o", ".ddx");
        result.DdxTextureCount = (ddxPath != null ? 1 : 0) + (ddxObjPath != null ? 1 : 0);

        // Perform matching analysis
        if (levelPsx != null)
        {
            AnalyzeMatching(levelDdm, levelPsx, out var placed, out var unmatched, out var unplaced);
            result.LevelMatched = placed.Count;
            result.LevelUnmatched = unmatched.Count;
            result.LevelUnplaced = unplaced.Count;
            result.LevelPlaced = placed;
            result.LevelUnmatchedEntries = unmatched;
            result.LevelUnplacedEntries = unplaced;
            result.LevelBounds = ComputeBounds(placed);
            result.LevelOutliers = FindOutliers(placed);
        }

        if (objectsDdm != null)
        {
            var objPsx = objectsPsx ?? levelPsx;
            if (objPsx != null)
            {
                AnalyzeMatching(objectsDdm, objPsx, out var placed, out var unmatched, out var unplaced);
                result.ObjectsMatched = placed.Count;
                result.ObjectsUnmatched = unmatched.Count;
                result.ObjectsUnplaced = unplaced.Count;
                result.ObjectsPlaced = placed;
                result.ObjectsUnmatchedEntries = unmatched;
                result.ObjectsUnplacedEntries = unplaced;
                result.ObjectsBounds = ComputeBounds(placed);
                result.ObjectsOutliers = FindOutliers(placed);
            }
        }

        return result;
    }

    private static void AnalyzeMatching(
        DdmFile ddm,
        PsxLayoutFile psx,
        out List<PlacedEntry> placed,
        out List<UnmatchedEntry> unmatched,
        out List<UnplacedEntry> unplaced)
    {
        var ddmByHash = DdmHashLookup.Build(ddm);
        var meshIndexToDdm = DdmHashLookup.ResolveMeshIndices(psx, ddmByHash);

        placed = [];
        unmatched = [];
        var placedIndices = new HashSet<int>();
        var seenUnmatchedHashes = new HashSet<uint>();

        foreach (var psxObj in psx.Objects)
        {
            if (psxObj.MeshIndex >= psx.MeshNameHashes.Length)
                continue;

            var nameHash = psx.MeshNameHashes[psxObj.MeshIndex];
            if (!meshIndexToDdm.TryGetValue(psxObj.MeshIndex, out var ddmIdx))
            {
                if (seenUnmatchedHashes.Add(nameHash))
                {
                    unmatched.Add(new UnmatchedEntry(
                        nameHash, psxObj.MeshIndex, psxObj.X, psxObj.Y, psxObj.Z,
                        QbKey.TryResolve(nameHash)));
                }
                continue;
            }

            placedIndices.Add(ddmIdx);
            placed.Add(new PlacedEntry(
                ddm.Objects[ddmIdx].Name, nameHash,
                psxObj.X, psxObj.Y, psxObj.Z, ddmIdx));
        }

        // Find DDM objects never referenced by any PSX entry
        unplaced = [];
        for (var i = 0; i < ddm.Objects.Count; i++)
        {
            if (placedIndices.Contains(i)) continue;
            var obj = ddm.Objects[i];
            if (obj.Vertices.Count == 0 || obj.Indices.Length == 0) continue;
            unplaced.Add(new UnplacedEntry(obj.Name, obj.Checksum, i));
        }
    }

    private static BoundingBox? ComputeBounds(List<PlacedEntry> placed)
    {
        if (placed.Count == 0) return null;

        var minX = float.MaxValue; var minY = float.MaxValue; var minZ = float.MaxValue;
        var maxX = float.MinValue; var maxY = float.MinValue; var maxZ = float.MinValue;

        foreach (var p in placed)
        {
            minX = MathF.Min(minX, p.X); maxX = MathF.Max(maxX, p.X);
            minY = MathF.Min(minY, p.Y); maxY = MathF.Max(maxY, p.Y);
            minZ = MathF.Min(minZ, p.Z); maxZ = MathF.Max(maxZ, p.Z);
        }

        return new BoundingBox(minX, minY, minZ, maxX, maxY, maxZ);
    }

    /// <summary>
    /// Finds position outliers using median absolute deviation (MAD).
    /// Objects beyond 3× MAD from the median on any axis are flagged.
    /// </summary>
    private static List<PlacedEntry> FindOutliers(List<PlacedEntry> placed)
    {
        if (placed.Count < 4) return [];

        var xs = placed.Select(p => p.X).ToArray();
        var ys = placed.Select(p => p.Y).ToArray();
        var zs = placed.Select(p => p.Z).ToArray();

        var medX = Median(xs);
        var medY = Median(ys);
        var medZ = Median(zs);

        var madX = Mad(xs, medX);
        var madY = Mad(ys, medY);
        var madZ = Mad(zs, medZ);

        // Use 3× MAD as threshold (robust outlier detection)
        const float threshold = 3f;
        var outliers = new List<PlacedEntry>();

        // Deduplicate by DDM index (same mesh placed multiple times shouldn't be listed repeatedly)
        var seenIndices = new HashSet<int>();
        foreach (var p in placed)
        {
            if (!seenIndices.Add(p.DdmIndex)) continue;

            var devX = madX > 0 ? MathF.Abs(p.X - medX) / madX : 0;
            var devY = madY > 0 ? MathF.Abs(p.Y - medY) / madY : 0;
            var devZ = madZ > 0 ? MathF.Abs(p.Z - medZ) / madZ : 0;

            if (devX > threshold || devY > threshold || devZ > threshold)
                outliers.Add(p);
        }

        return outliers;
    }

    private static float Median(float[] values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2
            : sorted[mid];
    }

    private static float Mad(float[] values, float median)
    {
        var deviations = values.Select(v => MathF.Abs(v - median)).ToArray();
        return Median(deviations) * 1.4826f; // Scale factor for normal distribution
    }

    private static void PrintDetailedReport(LevelAnalysis result)
    {
        var rule = new Rule($"[bold]{result.LevelName}[/]");
        rule.LeftJustified();
        AnsiConsole.Write(rule);

        // File inventory
        var inventoryTable = new Table()
            .Border(TableBorder.Simple)
            .Title("[bold]File Inventory[/]")
            .AddColumn("Item")
            .AddColumn("Value", c => c.RightAligned());

        inventoryTable.AddRow("Level DDM objects", result.LevelDdmCount.ToString());
        inventoryTable.AddRow("Objects DDM objects", result.ObjectsDdmCount.ToString());
        inventoryTable.AddRow("Level PSX objects", result.LevelPsxCount.ToString());
        inventoryTable.AddRow("Level PSX mesh hashes", result.LevelMeshHashes.ToString());
        inventoryTable.AddRow("Objects PSX objects", result.ObjectsPsxCount.ToString());
        inventoryTable.AddRow("Objects PSX mesh hashes", result.ObjectsMeshHashes.ToString());
        inventoryTable.AddRow("DDX archives", result.DdxTextureCount.ToString());
        inventoryTable.AddRow(".lit file", result.HasLitFile ? $"yes ({result.LightCount} lights)" : "no");
        AnsiConsole.Write(inventoryTable);

        // Level matching
        if (result.LevelPsxCount > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Level Placement (DDM x Level PSX)[/]");
            PrintMatchingDetails(result.LevelPlaced, result.LevelUnmatchedEntries,
                result.LevelUnplacedEntries, result.LevelBounds, result.LevelOutliers);
        }

        // Objects matching
        if (result.ObjectsDdmCount > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Objects Placement (Objects DDM x Objects PSX)[/]");
            PrintMatchingDetails(result.ObjectsPlaced, result.ObjectsUnmatchedEntries,
                result.ObjectsUnplacedEntries, result.ObjectsBounds, result.ObjectsOutliers);
        }
    }

    private static void PrintMatchingDetails(
        List<PlacedEntry> placed,
        List<UnmatchedEntry> unmatched,
        List<UnplacedEntry> unplaced,
        BoundingBox? bounds,
        List<PlacedEntry> outliers)
    {
        // Unique placed DDM objects (dedup by index since same mesh can be instanced)
        var uniquePlaced = placed.DistinctBy(p => p.DdmIndex).ToList();
        AnsiConsole.MarkupLine(
            $"  Matched: [green]{uniquePlaced.Count}[/] DDM objects " +
            $"({placed.Count} instances via {placed.DistinctBy(p => p.Hash).Count()} unique hashes)");

        if (unmatched.Count > 0)
        {
            AnsiConsole.MarkupLine($"  Unmatched PSX hashes: [yellow]{unmatched.Count}[/] (mesh in PSX, no DDM equivalent)");
            var unmatchedTable = new Table()
                .Border(TableBorder.Simple)
                .AddColumn("Hash")
                .AddColumn("MeshIdx", c => c.RightAligned())
                .AddColumn("Name")
                .AddColumn("Position");

            foreach (var u in unmatched.Take(20))
            {
                unmatchedTable.AddRow(
                    $"0x{u.Hash:X8}",
                    u.MeshIndex.ToString(),
                    u.ResolvedName ?? "[dim]???[/]",
                    $"({u.X:F0}, {u.Y:F0}, {u.Z:F0})");
            }
            if (unmatched.Count > 20)
                unmatchedTable.AddRow("[dim]...[/]", "", "",
                    $"[dim]+{unmatched.Count - 20} more[/]");
            AnsiConsole.Write(unmatchedTable);
        }

        if (unplaced.Count > 0)
        {
            AnsiConsole.MarkupLine($"  Unplaced DDM objects: [yellow]{unplaced.Count}[/] (in DDM, not referenced by PSX)");
            var unplacedTable = new Table()
                .Border(TableBorder.Simple)
                .AddColumn("Index", c => c.RightAligned())
                .AddColumn("Name")
                .AddColumn("Checksum");

            foreach (var u in unplaced.Take(20))
            {
                unplacedTable.AddRow(
                    u.DdmIndex.ToString(),
                    u.Name,
                    $"0x{u.Checksum:X8}");
            }
            if (unplaced.Count > 20)
                unplacedTable.AddRow("", $"[dim]+{unplaced.Count - 20} more[/]", "");
            AnsiConsole.Write(unplacedTable);
        }

        // Position analysis
        if (bounds != null)
        {
            var b = bounds.Value;
            AnsiConsole.MarkupLine(
                $"  Bounding box: X[[{b.MinX:F0}..{b.MaxX:F0}]] " +
                $"Y[[{b.MinY:F0}..{b.MaxY:F0}]] Z[[{b.MinZ:F0}..{b.MaxZ:F0}]]");
            AnsiConsole.MarkupLine(
                $"  Center: ({b.Center.X:F0}, {b.Center.Y:F0}, {b.Center.Z:F0}) " +
                $"Extent: ({b.Extent.X:F0}, {b.Extent.Y:F0}, {b.Extent.Z:F0})");
        }

        if (outliers.Count > 0)
        {
            AnsiConsole.MarkupLine($"  [bold yellow]Position outliers ({outliers.Count}):[/]");
            var outlierTable = new Table()
                .Border(TableBorder.Simple)
                .AddColumn("Name")
                .AddColumn("Hash")
                .AddColumn("Position")
                .AddColumn("Distance from center", c => c.RightAligned());

            var center = bounds?.Center ?? Vector3.Zero;
            foreach (var o in outliers)
            {
                var pos = new Vector3(o.X, o.Y, o.Z);
                var dist = Vector3.Distance(pos, center);
                outlierTable.AddRow(
                    $"[yellow]{o.Name}[/]",
                    $"0x{o.Hash:X8}",
                    $"({o.X:F0}, {o.Y:F0}, {o.Z:F0})",
                    $"{dist:F0}");
            }
            AnsiConsole.Write(outlierTable);
        }
        else if (placed.Count > 0)
        {
            AnsiConsole.MarkupLine("  [green]No position outliers detected[/]");
        }
    }

    private static string? FindCompanionFile(string directory, string stem, string extension)
    {
        var files = Directory.GetFiles(directory, stem + extension,
            new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive });
        return files.Length > 0 ? files[0] : null;
    }
}
