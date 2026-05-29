using System.CommandLine;
using System.Numerics;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Ddm;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using Spectre.Console;

namespace DdmAnalyzer.Commands;

/// <summary>
/// Verifies object placement by computing final glTF positions (after coordinate mapping)
/// and checking whether objects fall within the level geometry bounding box.
/// </summary>
public static class VerifyCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a level directory or parent Levels/ directory with --all"
        };
        var allOption = new Option<bool>("--all")
        {
            Description = "Process all level subdirectories under the given parent directory"
        };

        var command = new Command("verify", "Verify object placement: compute final glTF positions, check bounds overlap");
        command.Arguments.Add(inputArgument);
        command.Options.Add(allOption);

        command.SetAction((parseResult, _) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var all = parseResult.GetValue(allOption);

            if (all)
                return Task.FromResult(VerifyAll(input));
            return Task.FromResult(VerifyLevel(input));
        });

        return command;
    }

    private static int VerifyAll(string parentDir)
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

        var summaryTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Placement Verification Summary[/]")
            .AddColumn("Level")
            .AddColumn("Level Objs", c => c.RightAligned())
            .AddColumn("Obj Objs", c => c.RightAligned())
            .AddColumn("In Bounds", c => c.RightAligned())
            .AddColumn("Outside", c => c.RightAligned())
            .AddColumn("Outside Names");

        foreach (var dir in levelDirs)
        {
            var result = AnalyzeLevel(dir);
            if (result == null) continue;

            var outsideNames = result.OutsideBoundsObjects.Count > 0
                ? string.Join(", ", result.OutsideBoundsObjects.Select(o => o.Name).Distinct().Take(5))
                : "[dim]-[/]";
            var outsideStr = result.OutsideBoundsObjects.Count > 0
                ? $"[yellow]{result.OutsideBoundsObjects.Count}[/]"
                : "0";

            summaryTable.AddRow(
                result.LevelName,
                result.LevelObjectCount.ToString(),
                result.ObjectsObjectCount.ToString(),
                result.InBoundsObjects.Count.ToString(),
                outsideStr,
                outsideNames);
        }

        AnsiConsole.Write(summaryTable);
        return 0;
    }

    private static int VerifyLevel(string levelDir)
    {
        if (!Directory.Exists(levelDir))
        {
            AnsiConsole.MarkupLine($"[red]Directory not found:[/] {levelDir}");
            return 1;
        }

        var result = AnalyzeLevel(levelDir);
        if (result == null)
        {
            AnsiConsole.MarkupLine("[yellow]No DDM + PSX files found.[/]");
            return 0;
        }

        PrintDetailedReport(result);
        return 0;
    }

    private sealed class VerifyResult
    {
        public required string LevelName { get; init; }
        public int LevelObjectCount { get; set; }
        public int ObjectsObjectCount { get; set; }

        // Level geometry bounds in glTF space
        public Vector3 LevelMin { get; set; }
        public Vector3 LevelMax { get; set; }
        public Vector3 LevelCenter { get; set; }

        // Padded bounds for object verification (10% margin)
        public Vector3 PaddedMin { get; set; }
        public Vector3 PaddedMax { get; set; }

        // Object positions in glTF space
        public List<ObjectPosition> InBoundsObjects { get; set; } = [];
        public List<ObjectPosition> OutsideBoundsObjects { get; set; } = [];
    }

    private record struct ObjectPosition(
        string Name, string Source,
        float PsxX, float PsxY, float PsxZ,
        float GltfX, float GltfY, float GltfZ,
        float DistFromCenter);

    private static VerifyResult? AnalyzeLevel(string levelDir)
    {
        var ddmFiles = Directory.GetFiles(levelDir, "*.ddm",
            new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive });
        if (ddmFiles.Length == 0) return null;

        var levelDdmPath = ddmFiles.FirstOrDefault(f =>
            !Path.GetFileNameWithoutExtension(f).EndsWith("_o", StringComparison.OrdinalIgnoreCase));
        if (levelDdmPath == null) return null;

        var levelName = Path.GetFileNameWithoutExtension(levelDdmPath);

        // Parse level PSX
        var levelPsxPath = FindFile(levelDir, levelName + ".psx");
        if (levelPsxPath == null) return null;
        var levelPsx = PsxLayoutFile.Parse(levelPsxPath);
        if (levelPsx == null) return null;

        // Parse level DDM
        var levelDdm = DdmFile.Parse(levelDdmPath);

        var result = new VerifyResult
        {
            LevelName = levelName,
            LevelObjectCount = levelDdm.Objects.Count,
        };

        // Compute level geometry bounding box in glTF coordinates
        // Using the same mapping as GltfWriter: translation = (-psxX, -psxY, +psxZ)
        var levelMeshMap = DdmHashLookup.ResolveMeshIndices(levelPsx, DdmHashLookup.Build(levelDdm));
        ComputeLevelBounds(levelDdm, levelPsx, levelMeshMap, result);

        // Parse objects
        var objectsDdmPath = FindFile(levelDir, levelName + "_o.ddm");
        var objectsPsxPath = FindFile(levelDir, levelName + "_o.psx");

        DdmFile? objectsDdm = null;
        PsxLayoutFile? objectsPsx = null;

        if (objectsDdmPath != null)
        {
            objectsDdm = DdmFile.Parse(objectsDdmPath);
            result.ObjectsObjectCount = objectsDdm.Objects.Count;
        }
        if (objectsPsxPath != null)
            objectsPsx = PsxLayoutFile.Parse(objectsPsxPath);

        // Check object positions against level bounds
        if (objectsDdm != null)
        {
            var objPsx = objectsPsx ?? levelPsx;
            var objMeshMap = DdmHashLookup.ResolveMeshIndices(objPsx, DdmHashLookup.Build(objectsDdm));
            CheckObjectPositions(objectsDdm, objPsx, objMeshMap, result, "Objects");
        }

        return result;
    }

    private static void ComputeLevelBounds(
        DdmFile ddm, PsxLayoutFile psx, Dictionary<int, int> meshIndexToDdm, VerifyResult result)
    {
        var minX = float.MaxValue; var minY = float.MaxValue; var minZ = float.MaxValue;
        var maxX = float.MinValue; var maxY = float.MinValue; var maxZ = float.MinValue;
        var count = 0;

        foreach (var psxObj in psx.Objects)
        {
            if (psxObj.MeshIndex >= psx.MeshNameHashes.Length)
                continue;
            if (!meshIndexToDdm.TryGetValue(psxObj.MeshIndex, out var ddmIdx))
                continue;

            var ddmObj = ddm.Objects[ddmIdx];
            if (ddmObj.Vertices.Count == 0) continue;

            var tx = -psxObj.X;
            var ty = -psxObj.Y;
            var tz = psxObj.Z;

            foreach (var v in ddmObj.Vertices)
            {
                var gx = -v.X + tx;
                var gy = -v.Y + ty;
                var gz = v.Z + tz;

                minX = MathF.Min(minX, gx); maxX = MathF.Max(maxX, gx);
                minY = MathF.Min(minY, gy); maxY = MathF.Max(maxY, gy);
                minZ = MathF.Min(minZ, gz); maxZ = MathF.Max(maxZ, gz);
            }
            count++;
        }

        if (count == 0) return;

        result.LevelMin = new Vector3(minX, minY, minZ);
        result.LevelMax = new Vector3(maxX, maxY, maxZ);
        result.LevelCenter = (result.LevelMin + result.LevelMax) / 2;

        var extent = result.LevelMax - result.LevelMin;
        var padding = extent * 0.1f;
        result.PaddedMin = result.LevelMin - padding;
        result.PaddedMax = result.LevelMax + padding;
    }

    private static void CheckObjectPositions(
        DdmFile ddm, PsxLayoutFile psx, Dictionary<int, int> meshIndexToDdm,
        VerifyResult result, string source)
    {
        foreach (var psxObj in psx.Objects)
        {
            if (psxObj.MeshIndex >= psx.MeshNameHashes.Length)
                continue;
            if (!meshIndexToDdm.TryGetValue(psxObj.MeshIndex, out var ddmIdx))
                continue;

            var ddmObj = ddm.Objects[ddmIdx];

            // Compute glTF position: (-X, -Y, +Z) coordinate mapping
            var gx = -psxObj.X;
            var gy = -psxObj.Y;
            var gz = psxObj.Z;

            var dist = Vector3.Distance(new Vector3(gx, gy, gz), result.LevelCenter);

            var pos = new ObjectPosition(
                ddmObj.Name, source,
                psxObj.X, psxObj.Y, psxObj.Z,
                gx, gy, gz, dist);

            // Check if within padded level bounds
            if (gx >= result.PaddedMin.X && gx <= result.PaddedMax.X &&
                gy >= result.PaddedMin.Y && gy <= result.PaddedMax.Y &&
                gz >= result.PaddedMin.Z && gz <= result.PaddedMax.Z)
            {
                result.InBoundsObjects.Add(pos);
            }
            else
            {
                result.OutsideBoundsObjects.Add(pos);
            }
        }
    }

    private static void PrintDetailedReport(VerifyResult result)
    {
        var rule = new Rule($"[bold]{result.LevelName} — Placement Verification[/]");
        rule.LeftJustified();
        AnsiConsole.Write(rule);

        // Level bounds
        AnsiConsole.MarkupLine($"  [bold]Level geometry bounds (glTF space):[/]");
        AnsiConsole.MarkupLine(
            $"    Min: ({result.LevelMin.X:F0}, {result.LevelMin.Y:F0}, {result.LevelMin.Z:F0})");
        AnsiConsole.MarkupLine(
            $"    Max: ({result.LevelMax.X:F0}, {result.LevelMax.Y:F0}, {result.LevelMax.Z:F0})");
        AnsiConsole.MarkupLine(
            $"    Center: ({result.LevelCenter.X:F0}, {result.LevelCenter.Y:F0}, {result.LevelCenter.Z:F0})");
        var extent = result.LevelMax - result.LevelMin;
        AnsiConsole.MarkupLine(
            $"    Extent: ({extent.X:F0}, {extent.Y:F0}, {extent.Z:F0})");
        AnsiConsole.MarkupLine(
            $"    Padded (±10%): ({result.PaddedMin.X:F0}..{result.PaddedMax.X:F0}, " +
            $"{result.PaddedMin.Y:F0}..{result.PaddedMax.Y:F0}, " +
            $"{result.PaddedMin.Z:F0}..{result.PaddedMax.Z:F0})");

        // In-bounds objects
        AnsiConsole.MarkupLine($"\n  [green]In-bounds objects: {result.InBoundsObjects.Count}[/]");
        if (result.InBoundsObjects.Count > 0)
        {
            var table = new Table()
                .Border(TableBorder.Simple)
                .AddColumn("Name")
                .AddColumn("PSX Pos")
                .AddColumn("glTF Pos")
                .AddColumn("Dist", c => c.RightAligned());

            foreach (var obj in result.InBoundsObjects)
            {
                table.AddRow(
                    obj.Name,
                    $"({obj.PsxX:F0}, {obj.PsxY:F0}, {obj.PsxZ:F0})",
                    $"({obj.GltfX:F0}, {obj.GltfY:F0}, {obj.GltfZ:F0})",
                    $"{obj.DistFromCenter:F0}");
            }
            AnsiConsole.Write(table);
        }

        // Outside-bounds objects
        if (result.OutsideBoundsObjects.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"\n  [bold yellow]Outside-bounds objects: {result.OutsideBoundsObjects.Count}[/]");
            var table = new Table()
                .Border(TableBorder.Simple)
                .AddColumn("Name")
                .AddColumn("PSX Pos")
                .AddColumn("glTF Pos")
                .AddColumn("Dist", c => c.RightAligned())
                .AddColumn("Issue");

            foreach (var obj in result.OutsideBoundsObjects)
            {
                var issues = new List<string>();
                if (obj.GltfX < result.PaddedMin.X || obj.GltfX > result.PaddedMax.X)
                    issues.Add($"X outside");
                if (obj.GltfY < result.PaddedMin.Y || obj.GltfY > result.PaddedMax.Y)
                    issues.Add($"Y outside");
                if (obj.GltfZ < result.PaddedMin.Z || obj.GltfZ > result.PaddedMax.Z)
                    issues.Add($"Z outside");

                table.AddRow(
                    $"[yellow]{obj.Name}[/]",
                    $"({obj.PsxX:F0}, {obj.PsxY:F0}, {obj.PsxZ:F0})",
                    $"({obj.GltfX:F0}, {obj.GltfY:F0}, {obj.GltfZ:F0})",
                    $"{obj.DistFromCenter:F0}",
                    string.Join(", ", issues));
            }
            AnsiConsole.Write(table);
        }
        else
        {
            AnsiConsole.MarkupLine("\n  [green]All objects are within level bounds[/]");
        }
    }

    private static string? FindFile(string directory, string name)
    {
        var files = Directory.GetFiles(directory, name,
            new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive });
        return files.Length > 0 ? files[0] : null;
    }
}
