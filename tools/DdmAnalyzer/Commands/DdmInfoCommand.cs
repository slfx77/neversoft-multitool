using System.CommandLine;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Ddm;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.QbKey;
using Spectre.Console;

namespace DdmAnalyzer.Commands;

public static class DdmInfoCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a .DDM file or level directory containing DDM + PSX files"
        };
        var psxOption = new Option<string?>("-p", "--psx")
        {
            Description = "Path to companion PSX file for position comparison"
        };

        var command = new Command("ddm-info", "Dump DDM object metadata: bounding boxes, vertex ranges, and PSX position comparison");
        command.Arguments.Add(inputArgument);
        command.Options.Add(psxOption);

        command.SetAction((parseResult, _) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var psxPath = parseResult.GetValue(psxOption);

            return Task.FromResult(Execute(input, psxPath));
        });

        return command;
    }

    private static int Execute(string input, string? psxPath)
    {
        // If input is a directory, find DDM + PSX files automatically
        string? ddmPath = null;
        string? objectsDdmPath = null;
        string? levelPsxPath = psxPath;
        string? objectsPsxPath = null;

        if (Directory.Exists(input))
        {
            var ddmFiles = Directory.GetFiles(input, "*.ddm",
                new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive });
            ddmPath = ddmFiles.FirstOrDefault(f =>
                !Path.GetFileNameWithoutExtension(f).EndsWith("_o", StringComparison.OrdinalIgnoreCase));
            objectsDdmPath = ddmFiles.FirstOrDefault(f =>
                Path.GetFileNameWithoutExtension(f).EndsWith("_o", StringComparison.OrdinalIgnoreCase));

            if (ddmPath == null)
            {
                AnsiConsole.MarkupLine("[red]No level DDM file found in directory.[/]");
                return 1;
            }

            var levelName = Path.GetFileNameWithoutExtension(ddmPath);
            levelPsxPath ??= FindFile(input, levelName + ".psx");
            objectsPsxPath = FindFile(input, levelName + "_o.psx");
        }
        else if (File.Exists(input))
        {
            ddmPath = input;
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Not found:[/] {input}");
            return 1;
        }

        // Dump level DDM
        DumpDdmFile(ddmPath, levelPsxPath, "Level");

        // Dump objects DDM if found
        if (objectsDdmPath != null)
        {
            AnsiConsole.WriteLine();
            DumpDdmFile(objectsDdmPath, objectsPsxPath ?? levelPsxPath, "Objects");
        }

        return 0;
    }

    private static void DumpDdmFile(string ddmPath, string? psxPath, string label)
    {
        var filename = Path.GetFileName(ddmPath);
        var rule = new Rule($"[bold]{label}: {filename}[/]");
        rule.LeftJustified();
        AnsiConsole.Write(rule);

        DdmFile ddm;
        try { ddm = DdmFile.Parse(ddmPath); }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Parse error: {ex.Message}[/]");
            return;
        }

        AnsiConsole.MarkupLine($"  Objects: [cyan]{ddm.Objects.Count}[/]");

        // Load PSX for comparison
        PsxLayoutFile? psx = null;
        if (psxPath != null)
        {
            try { psx = PsxLayoutFile.Parse(psxPath); }
            catch { /* ignore */ }
        }

        // Build PSX hash → position lookup
        var psxPositions = new Dictionary<uint, (float X, float Y, float Z)>();
        if (psx != null)
        {
            foreach (var obj in psx.Objects)
            {
                if (obj.MeshIndex < psx.MeshNameHashes.Length)
                {
                    var hash = psx.MeshNameHashes[obj.MeshIndex];
                    psxPositions.TryAdd(hash, (obj.X, obj.Y, obj.Z));
                }
            }
        }

        // Object info table
        var table = new Table()
            .Border(TableBorder.Simple)
            .AddColumn("#", c => c.RightAligned())
            .AddColumn("Name")
            .AddColumn("BBox Center")
            .AddColumn("BBox Extent")
            .AddColumn("Vtx Min")
            .AddColumn("Vtx Max");

        if (psx != null)
        {
            table.AddColumn("PSX Pos");
            table.AddColumn("Delta (BBox-PSX)");
        }

        for (var i = 0; i < ddm.Objects.Count; i++)
        {
            var obj = ddm.Objects[i];
            var center = $"({obj.BBoxCenterX:F1}, {obj.BBoxCenterY:F1}, {obj.BBoxCenterZ:F1})";
            var extent = $"({obj.BBoxExtentX:F1}, {obj.BBoxExtentY:F1}, {obj.BBoxExtentZ:F1})";

            // Compute actual vertex min/max
            var (vMin, vMax) = ComputeVertexBounds(obj);
            var vtxMin = $"({vMin.x:F1}, {vMin.y:F1}, {vMin.z:F1})";
            var vtxMax = $"({vMax.x:F1}, {vMax.y:F1}, {vMax.z:F1})";

            if (psx != null)
            {
                // Try to find PSX position for this object
                var nameHash = QbKey.Hash(obj.Name);
                var hash = obj.Checksum != 0 ? obj.Checksum : nameHash;

                if (psxPositions.TryGetValue(hash, out var pos) ||
                    (obj.Checksum != 0 && psxPositions.TryGetValue(nameHash, out pos)))
                {
                    var psxStr = $"({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1})";
                    var dx = obj.BBoxCenterX - pos.X;
                    var dy = obj.BBoxCenterY - pos.Y;
                    var dz = obj.BBoxCenterZ - pos.Z;
                    var deltaStr = $"({dx:F1}, {dy:F1}, {dz:F1})";
                    table.AddRow(i.ToString(), obj.Name, center, extent, vtxMin, vtxMax, psxStr, deltaStr);
                }
                else
                {
                    table.AddRow(i.ToString(), obj.Name, center, extent, vtxMin, vtxMax,
                        "[dim]no match[/]", "[dim]-[/]");
                }
            }
            else
            {
                table.AddRow(i.ToString(), obj.Name, center, extent, vtxMin, vtxMax);
            }
        }

        AnsiConsole.Write(table);

        // Summary: are vertices local-space or world-space?
        if (psx != null && ddm.Objects.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnalyzeCoordinateSpace(ddm, psxPositions);
        }
    }

    private static void AnalyzeCoordinateSpace(DdmFile ddm, Dictionary<uint, (float X, float Y, float Z)> psxPositions)
    {
        var localCount = 0;
        var worldCount = 0;
        var totalMatched = 0;

        foreach (var obj in ddm.Objects)
        {
            if (obj.Vertices.Count == 0) continue;

            var nameHash = QbKey.Hash(obj.Name);
            var hash = obj.Checksum != 0 ? obj.Checksum : nameHash;
            if (!psxPositions.TryGetValue(hash, out var pos) &&
                !(obj.Checksum != 0 && psxPositions.TryGetValue(nameHash, out pos)))
                continue;

            totalMatched++;

            // Check if bbox center is close to origin (local space) or close to PSX position (world space)
            var centerDist = MathF.Sqrt(
                obj.BBoxCenterX * obj.BBoxCenterX +
                obj.BBoxCenterY * obj.BBoxCenterY +
                obj.BBoxCenterZ * obj.BBoxCenterZ);

            var dx = obj.BBoxCenterX - pos.X;
            var dy = obj.BBoxCenterY - pos.Y;
            var dz = obj.BBoxCenterZ - pos.Z;
            var psxDist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

            // If bbox center is closer to PSX position than to origin, it's world-space
            if (psxDist < centerDist && centerDist > 10f)
                worldCount++;
            else if (centerDist < 100f)
                localCount++;
        }

        AnsiConsole.MarkupLine($"  [bold]Coordinate space analysis[/] ({totalMatched} matched objects):");
        AnsiConsole.MarkupLine($"    Near-origin (local space): [cyan]{localCount}[/]");
        AnsiConsole.MarkupLine($"    Near-PSX-position (world space): [cyan]{worldCount}[/]");

        if (worldCount > localCount)
            AnsiConsole.MarkupLine("    [yellow]DDM vertices appear to be in WORLD SPACE — PSX translations should NOT be applied[/]");
        else if (localCount > worldCount)
            AnsiConsole.MarkupLine("    [green]DDM vertices appear to be in LOCAL SPACE — PSX translations should be applied[/]");
        else
            AnsiConsole.MarkupLine("    [yellow]Inconclusive — manual inspection needed[/]");
    }

    private static ((float x, float y, float z) min, (float x, float y, float z) max) ComputeVertexBounds(DdmObject obj)
    {
        if (obj.Vertices.Count == 0)
            return ((0, 0, 0), (0, 0, 0));

        var minX = float.MaxValue; var minY = float.MaxValue; var minZ = float.MaxValue;
        var maxX = float.MinValue; var maxY = float.MinValue; var maxZ = float.MinValue;

        foreach (var v in obj.Vertices)
        {
            if (v.X < minX) minX = v.X;
            if (v.Y < minY) minY = v.Y;
            if (v.Z < minZ) minZ = v.Z;
            if (v.X > maxX) maxX = v.X;
            if (v.Y > maxY) maxY = v.Y;
            if (v.Z > maxZ) maxZ = v.Z;
        }

        return ((minX, minY, minZ), (maxX, maxY, maxZ));
    }

    private static string? FindFile(string directory, string name)
    {
        var files = Directory.GetFiles(directory, name,
            new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive });
        return files.Length > 0 ? files[0] : null;
    }
}
