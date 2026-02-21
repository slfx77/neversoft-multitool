using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Mesh;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class DdmCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to directory containing .ddm files, or parent directory with --all"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for glTF (.glb) files",
            DefaultValueFactory = _ => "TestOutput/DDM"
        };
        var texturesOption = new Option<string>("-t", "--textures")
        {
            Description = "Path to directory with extracted DDX textures (PNG) for material binding"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };
        var ddxOption = new Option<string>("-d", "--ddx")
        {
            Description = "Path to DDX texture archive directory for material binding"
        };
        var allOption = new Option<bool>("--all")
        {
            Description = "Process all level subdirectories under the given parent directory"
        };
        var psxOption = new Option<string>("-p", "--psx")
        {
            Description = "Path to PSX layout file or directory for placed level assembly"
        };

        var command = new Command("ddm", "Convert DDM mesh files to glTF (.glb)");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(texturesOption);
        command.Options.Add(verboseOption);
        command.Options.Add(ddxOption);
        command.Options.Add(allOption);
        command.Options.Add(psxOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var textures = parseResult.GetValue(texturesOption);
            var verbose = parseResult.GetValue(verboseOption);
            var ddx = parseResult.GetValue(ddxOption);
            var all = parseResult.GetValue(allOption);
            var psx = parseResult.GetValue(psxOption);

            if (all)
                return Task.FromResult(ExecuteAll(input, output, verbose));
            return Task.FromResult(Execute(input, output, textures, verbose, ddx, psx));
        });

        return command;
    }

    private static int ExecuteAll(string parentDir, string output, bool verbose)
    {
        if (!Directory.Exists(parentDir))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Directory not found: {parentDir}");
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

        AnsiConsole.MarkupLine($"Found [green]{levelDirs.Count}[/] level directories");

        var stopwatch = Stopwatch.StartNew();
        var totalConverted = 0;
        var totalErrors = 0;

        foreach (var dir in levelDirs)
        {
            var name = Path.GetFileName(dir);
            var levelOutput = Path.Combine(output, name);
            var exitCode = Execute(dir, levelOutput, null, verbose, null);
            if (exitCode == 0)
                totalConverted++;
            else
                totalErrors++;
        }

        stopwatch.Stop();
        var errorInfo = totalErrors > 0 ? $", [red]{totalErrors} errors[/]" : "";
        AnsiConsole.MarkupLine(
            $"\nBatch complete: [green]{totalConverted}[/]/{levelDirs.Count} levels{errorInfo} " +
            $"in {stopwatch.Elapsed.TotalSeconds:F2}s");

        return totalErrors > 0 ? 1 : 0;
    }

    private static int Execute(string input, string output, string? textures, bool verbose,
        string? ddxPath, string? psxPath = null)
    {
        if (!Directory.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Directory not found: {input}");
            return 1;
        }

        var allDdmFiles = Directory.GetFiles(input, "*.ddm",
            new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive });

        if (allDdmFiles.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No .ddm files found in the specified directory.[/]");
            return 0;
        }

        ddxPath = ResolveCompanionDir(input, ddxPath, "*.ddx", "DDX");
        psxPath = ResolveCompanionDir(input, psxPath, "*.psx", "PSX");

        var (placedLevels, objectDdmStems) = ClassifyDdmFiles(allDdmFiles, psxPath);
        var standaloneDdmFiles = allDdmFiles
            .Where(f => !placedLevels.ContainsKey(f) &&
                        !objectDdmStems.Contains(Path.GetFileNameWithoutExtension(f)))
            .ToArray();

        Directory.CreateDirectory(output);

        var textureInfo = textures != null && Directory.Exists(textures) ? $", textures={textures}" : "";
        var ddxInfo = ddxPath != null ? $", ddx={ddxPath}" : "";
        var placedInfo = placedLevels.Count > 0 ? $", [blue]{placedLevels.Count} placed level(s)[/]" : "";
        AnsiConsole.MarkupLine(
            $"Found [green]{allDdmFiles.Length}[/] DDM file(s){placedInfo}{textureInfo}{ddxInfo}");

        var stopwatch = Stopwatch.StartNew();
        var totalObjects = 0;
        var totalTriangles = 0;
        var converted = 0;

        foreach (var (ddmFile, levelPsx) in placedLevels)
        {
            var result = ConvertPlacedLevel(ddmFile, levelPsx, output, verbose, ddxPath, psxPath);
            totalObjects += result.Objects;
            totalTriangles += result.Triangles;
            if (result.Converted) converted++;
        }

        foreach (var file in standaloneDdmFiles)
        {
            var result = ConvertDdmFile(file, output, textures, verbose, ddxPath);
            totalObjects += result.Objects;
            totalTriangles += result.Triangles;
            if (result.Converted) converted++;
        }

        stopwatch.Stop();
        var totalFileCount = standaloneDdmFiles.Length + placedLevels.Count;
        AnsiConsole.MarkupLine(
            $"Converted [green]{converted}[/]/{totalFileCount} files " +
            $"({totalObjects:N0} objects, {totalTriangles:N0} triangles) " +
            $"in {stopwatch.Elapsed.TotalSeconds:F2}s");

        return 0;
    }

    /// <summary>
    /// Auto-detects a companion directory: checks input dir for matching files, then sibling dir.
    /// </summary>
    private static string? ResolveCompanionDir(string input, string? explicitPath,
        string searchPattern, string siblingName)
    {
        if (!string.IsNullOrEmpty(explicitPath))
            return explicitPath;
        var files = Directory.GetFiles(input, searchPattern,
            new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive });
        return files.Length > 0 ? input : ResolveSiblingDirectory(input, siblingName);
    }

    /// <summary>
    /// Classifies DDM files into placed levels (have PSX companion) and object companions (_o suffix).
    /// </summary>
    private static (Dictionary<string, string> PlacedLevels, HashSet<string> ObjectStems)
        ClassifyDdmFiles(string[] allDdmFiles, string? psxPath)
    {
        var placedLevels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var objectStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(psxPath))
            return (placedLevels, objectStems);

        foreach (var ddmFile in allDdmFiles)
        {
            var stem = Path.GetFileNameWithoutExtension(ddmFile);
            if (stem.EndsWith("_o", StringComparison.OrdinalIgnoreCase))
            {
                objectStems.Add(stem);
                continue;
            }

            var psx = FindCompanionFile(psxPath, stem, ".psx");
            if (psx != null)
                placedLevels[ddmFile] = psx;
        }

        return (placedLevels, objectStems);
    }

    private readonly record struct ConvertResult(int Objects, int Triangles, bool Converted);

    private static ConvertResult ConvertPlacedLevel(
        string levelDdmPath, string levelPsxPath, string output, bool verbose,
        string? ddxPath, string? psxDir)
    {
        var ddmName = Path.GetFileNameWithoutExtension(levelDdmPath);
        var inputDir = Path.GetDirectoryName(levelDdmPath)!;

        try
        {
            // Find _o companions (objects DDM + objects PSX)
            var objectsDdm = FindCompanionFile(inputDir, ddmName + "_o", ".ddm");
            var objectsPsx = psxDir != null ? FindCompanionFile(psxDir, ddmName + "_o", ".psx") : null;

            var (levelTris, objTris) = GltfWriter.WritePlacedLevel(
                levelDdmPath, levelPsxPath,
                objectsDdm, objectsPsx,
                output, ddmName, ddxPath);

            var totalTriangles = levelTris + objTris;

            if (verbose)
            {
                var objInfo = objectsDdm != null ? $" + objects" : "";
                AnsiConsole.MarkupLine(
                    $"  {ddmName}: [blue]placed level{objInfo}, {totalTriangles:N0} triangles[/]");
            }

            return new ConvertResult(0, totalTriangles, true);
        }
        catch (Exception ex)
        {
            if (verbose)
                AnsiConsole.MarkupLine($"  {ddmName}: [red]error: {ex.Message}[/]");
            return new ConvertResult(0, 0, false);
        }
    }

    private static ConvertResult ConvertDdmFile(
        string file, string output, string? textures, bool verbose, string? ddxPath)
    {
        var filename = Path.GetFileName(file);
        var ddmName = Path.GetFileNameWithoutExtension(filename);

        try
        {
            var ddm = DdmFile.Parse(file);

            // Load DDX textures
            Dictionary<string, byte[]>? ddxTextures = null;
            if (!string.IsNullOrEmpty(ddxPath) && Directory.Exists(ddxPath))
            {
                var ddx = FindCompanionFile(ddxPath, ddmName, ".ddx");
                if (ddx != null)
                    ddxTextures = DdxArchive.ReadAllEntries(ddx);
            }

            // Load .lit lights (search DDX dir, then input dir)
            var lights = FindAndParseLitFile(ddmName, ddxPath)
                      ?? FindAndParseLitFile(ddmName, Path.GetDirectoryName(file));

            var outputFile = Path.Combine(output, ddmName + ".glb");
            var triangles = GltfWriter.WriteDdm(ddm, outputFile, textures, ddmName, ddxTextures, lights);

            if (verbose)
            {
                var lightInfo = lights != null ? $", {lights.Count} lights" : "";
                AnsiConsole.MarkupLine(
                    $"  {filename}: [green]{ddm.Objects.Count} objects, {triangles:N0} triangles{lightInfo}[/]");
            }

            return new ConvertResult(ddm.Objects.Count, triangles, true);
        }
        catch (Exception ex)
        {
            if (verbose)
                AnsiConsole.MarkupLine($"  {filename}: [red]error: {ex.Message}[/]");
            return new ConvertResult(0, 0, false);
        }
    }

    private static List<LitLight>? FindAndParseLitFile(string levelName, string? searchDir)
    {
        if (string.IsNullOrEmpty(searchDir)) return null;
        var litPath = FindCompanionFile(searchDir, levelName, ".lit");
        if (litPath == null) return null;
        try { return LitFile.Parse(litPath); }
        catch { return null; }
    }

    private static string? ResolveSiblingDirectory(string inputDir, string siblingName)
    {
        var parent = Path.GetDirectoryName(inputDir);
        if (string.IsNullOrEmpty(parent)) return null;
        var sibling = Path.Combine(parent, siblingName);
        return Directory.Exists(sibling) ? sibling : null;
    }

    private static string? FindCompanionFile(string directory, string stem, string extension)
    {
        var files = Directory.GetFiles(directory, stem + extension,
            new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive });
        return files.Length > 0 ? files[0] : null;
    }
}
