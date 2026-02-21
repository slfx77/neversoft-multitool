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
        var psxOption = new Option<string>("-p", "--psx")
        {
            Description = "Path to companion PSX file or directory for world placement (auto-detected by name if omitted)"
        };
        var objectsOption = new Option<string>("--objects")
        {
            Description = "Path to companion _o.ddm objects file (auto-detected by name if omitted)"
        };
        var ddxOption = new Option<string>("-d", "--ddx")
        {
            Description = "Path to DDX texture archive directory for material binding"
        };
        var allOption = new Option<bool>("--all")
        {
            Description = "Process all level subdirectories under the given parent directory"
        };

        var command = new Command("ddm", "Convert DDM mesh files to glTF (.glb)");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(texturesOption);
        command.Options.Add(verboseOption);
        command.Options.Add(psxOption);
        command.Options.Add(objectsOption);
        command.Options.Add(ddxOption);
        command.Options.Add(allOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var textures = parseResult.GetValue(texturesOption);
            var verbose = parseResult.GetValue(verboseOption);
            var psx = parseResult.GetValue(psxOption);
            var objects = parseResult.GetValue(objectsOption);
            var ddx = parseResult.GetValue(ddxOption);
            var all = parseResult.GetValue(allOption);

            if (all)
                return Task.FromResult(ExecuteAll(input, output, verbose));
            return Task.FromResult(Execute(input, output, textures, verbose, psx, objects, ddx));
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
            var exitCode = Execute(dir, levelOutput, null, verbose, null, null, null);
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
        string? psxPath, string? objectsPath, string? ddxPath)
    {
        if (!Directory.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Directory not found: {input}");
            return 1;
        }

        var ddmFiles = Directory.GetFiles(input, "*.ddm",
            new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive });

        if (ddmFiles.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No .ddm files found in the specified directory.[/]");
            return 0;
        }

        // Auto-detect DDX directory: check input directory first, then sibling DDX/
        if (string.IsNullOrEmpty(ddxPath))
        {
            var ddxFiles = Directory.GetFiles(input, "*.ddx",
                new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive });
            ddxPath = ddxFiles.Length > 0 ? input : ResolveSiblingDirectory(input, "DDX");
        }

        Directory.CreateDirectory(output);

        var textureInfo = textures != null && Directory.Exists(textures)
            ? $", textures={textures}"
            : "";
        var ddxInfo = ddxPath != null ? $", ddx={ddxPath}" : "";
        AnsiConsole.MarkupLine($"Found [green]{ddmFiles.Length}[/] DDM file(s){textureInfo}{ddxInfo}");

        // Separate level DDMs from _o (object) DDMs — _o files are processed with their level
        var levelFiles = ddmFiles
            .Where(f => !Path.GetFileNameWithoutExtension(f)
                .EndsWith("_o", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var stopwatch = Stopwatch.StartNew();
        var totalObjects = 0;
        var totalTriangles = 0;
        var converted = 0;
        var placedLevels = 0;

        foreach (var file in levelFiles)
        {
            var result = ConvertDdmFile(file, output, textures, verbose, psxPath, objectsPath, ddxPath);
            totalObjects += result.Objects;
            totalTriangles += result.Triangles;
            if (result.Converted) converted++;
            if (result.Placed) placedLevels++;
        }

        stopwatch.Stop();
        var placedInfo = placedLevels > 0 ? $", {placedLevels} placed levels" : "";
        AnsiConsole.MarkupLine(
            $"Converted [green]{converted}[/]/{levelFiles.Count} files " +
            $"({totalObjects:N0} objects, {totalTriangles:N0} triangles{placedInfo}) " +
            $"in {stopwatch.Elapsed.TotalSeconds:F2}s");

        return 0;
    }

    private readonly record struct ConvertResult(
        int Objects, int Triangles, bool Converted, bool Placed);

    private static ConvertResult ConvertDdmFile(
        string file, string output, string? textures, bool verbose,
        string? explicitPsxPath, string? explicitObjectsPath, string? ddxPath)
    {
        var filename = Path.GetFileName(file);
        var ddmName = Path.GetFileNameWithoutExtension(filename);
        var inputDir = Path.GetDirectoryName(file)!;

        try
        {
            var ddm = DdmFile.Parse(file);

            // Resolve PSX file: explicit flag → explicit directory search → auto-detect in DDM directory
            var psxPath = ResolvePsxPath(explicitPsxPath, inputDir, ddmName);
            PsxLayoutFile? psxFile = null;
            if (psxPath != null)
            {
                try { psxFile = PsxLayoutFile.Parse(psxPath); }
                catch (Exception ex)
                {
                    if (verbose)
                        AnsiConsole.MarkupLine($"  [yellow]PSX parse failed: {ex.Message}[/]");
                }
            }

            if (psxFile != null)
                return ConvertPlacedLevel(ddm, ddmName, inputDir, output, textures, psxFile,
                    explicitObjectsPath, ddxPath, verbose);

            return ConvertStandalone(ddm, ddmName, filename, output, textures, ddxPath, verbose);
        }
        catch (Exception ex)
        {
            if (verbose)
                AnsiConsole.MarkupLine($"  {filename}: [red]error: {ex.Message}[/]");
            return new ConvertResult(0, 0, false, false);
        }
    }

    /// <summary>
    /// Resolves the PSX companion file path. If explicitPsx is a file, uses it directly.
    /// If it's a directory, searches for a matching file by DDM name. Otherwise auto-detects
    /// in the DDM's directory.
    /// </summary>
    private static string? ResolvePsxPath(string? explicitPsx, string inputDir, string ddmName)
    {
        if (!string.IsNullOrEmpty(explicitPsx))
        {
            if (File.Exists(explicitPsx))
                return explicitPsx;
            if (Directory.Exists(explicitPsx))
                return FindCompanionFile(explicitPsx, ddmName, ".psx");
        }

        return FindCompanionFile(inputDir, ddmName, ".psx");
    }

    private static ConvertResult ConvertPlacedLevel(
        DdmFile ddm, string ddmName, string inputDir, string output,
        string? textures, PsxLayoutFile psxFile, string? explicitObjectsPath,
        string? ddxPath, bool verbose)
    {
        // Resolve objects file: explicit flag → auto-detect in DDM directory
        var objectsFile = !string.IsNullOrEmpty(explicitObjectsPath) && File.Exists(explicitObjectsPath)
            ? explicitObjectsPath
            : FindCompanionFile(inputDir, ddmName + "_o", ".ddm");
        var objectsDdm = objectsFile != null ? DdmFile.Parse(objectsFile) : null;

        // Resolve objects PSX companion (_o.PSX) for object placement
        PsxLayoutFile? objectsPsxFile = null;
        var objectsPsxPath = FindCompanionFile(inputDir, ddmName + "_o", ".psx");
        if (objectsPsxPath != null)
        {
            try { objectsPsxFile = PsxLayoutFile.Parse(objectsPsxPath); }
            catch (Exception ex)
            {
                if (verbose)
                    AnsiConsole.MarkupLine($"  [yellow]Objects PSX parse failed: {ex.Message}[/]");
            }
        }

        var result = GltfWriter.WritePlacedLevel(
            ddm, objectsDdm, psxFile, objectsPsxFile, output, ddmName, textures, ddxPath);

        var objects = ddm.Objects.Count + (objectsDdm?.Objects.Count ?? 0);
        var triangles = result.Level + result.Objects;

        if (verbose)
        {
            var objPsxInfo = objectsPsxFile != null
                ? $", {objectsPsxFile.Objects.Count} obj PSX objects"
                : "";
            AnsiConsole.MarkupLine(
                $"  {ddmName}: [green]placed level[/] " +
                $"({ddm.Objects.Count} DDM meshes, {psxFile.Objects.Count} PSX objects{objPsxInfo}, " +
                $"{result.Level:N0} level tris, {result.Objects:N0} object tris)");
        }

        return new ConvertResult(objects, triangles, true, true);
    }

    private static ConvertResult ConvertStandalone(
        DdmFile ddm, string ddmName, string filename, string output,
        string? textures, string? ddxPath, bool verbose)
    {
        // Load DDX textures for standalone DDM
        Dictionary<string, byte[]>? ddxTextures = null;
        if (!string.IsNullOrEmpty(ddxPath) && Directory.Exists(ddxPath))
        {
            var ddx = FindCompanionFile(ddxPath, ddmName, ".ddx");
            if (ddx != null)
                ddxTextures = DdxArchive.ReadAllEntries(ddx);
        }

        var outputFile = Path.Combine(output, ddmName + ".glb");
        var triangles = GltfWriter.WriteDdm(ddm, outputFile, textures, ddmName, ddxTextures);

        if (verbose)
        {
            AnsiConsole.MarkupLine(
                $"  {filename}: [green]{ddm.Objects.Count} objects, {triangles:N0} triangles[/]");
        }

        return new ConvertResult(ddm.Objects.Count, triangles, true, false);
    }

    /// <summary>
    /// Looks for a sibling directory (e.g. DDX/ next to DDM/).
    /// </summary>
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
