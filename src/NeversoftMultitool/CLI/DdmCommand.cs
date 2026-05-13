using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
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
        var formatOption = MeshExportCliOptions.CreateFormatOption();
        var blenderHelperOption = MeshExportCliOptions.CreateBlenderHelperOption();

        var command = new Command("ddm", "Convert DDM mesh files to glTF (.glb) or Blender (.blend)");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(texturesOption);
        command.Options.Add(verboseOption);
        command.Options.Add(ddxOption);
        command.Options.Add(allOption);
        command.Options.Add(psxOption);
        command.Options.Add(formatOption);
        command.Options.Add(blenderHelperOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var textures = parseResult.GetValue(texturesOption);
            var verbose = parseResult.GetValue(verboseOption);
            var ddx = parseResult.GetValue(ddxOption);
            var all = parseResult.GetValue(allOption);
            var psx = parseResult.GetValue(psxOption);
            if (!MeshExportCliOptions.ValidateFormat(parseResult.GetValue(formatOption), out var format))
                return Task.FromResult(1);
            var blenderHelper = parseResult.GetValue(blenderHelperOption);

            if (all)
                return Task.FromResult(ExecuteAll(input, output, verbose, format, blenderHelper, cancellationToken));
            return Task.FromResult(Execute(input, output, textures, verbose, ddx, psx, format, blenderHelper,
                cancellationToken));
        });

        return command;
    }

    private static int ExecuteAll(
        string parentDir,
        string output,
        bool verbose,
        MeshOutputFormat format,
        string? blenderHelperPath,
        CancellationToken cancellationToken)
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
            var exitCode = Execute(
                dir,
                levelOutput,
                null,
                verbose,
                null,
                null,
                format,
                blenderHelperPath,
                cancellationToken);
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
        string? ddxPath, string? psxPath, MeshOutputFormat format,
        string? blenderHelperPath, CancellationToken cancellationToken)
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

        return ExecuteCoreExport(
            standaloneDdmFiles,
            placedLevels,
            output,
            verbose,
            textures,
            ddxPath,
            psxPath,
            format,
            blenderHelperPath,
            cancellationToken);
    }

    private static int ExecuteCoreExport(
        IReadOnlyList<string> standaloneDdmFiles,
        IReadOnlyDictionary<string, string> placedLevels,
        string output,
        bool verbose,
        string? texturePath,
        string? ddxPath,
        string? psxDir,
        MeshOutputFormat format,
        string? blenderHelperPath,
        CancellationToken cancellationToken)
    {
        var converted = 0;
        var failed = 0;
        var totalTriangles = 0;

        foreach (var (ddmFile, levelPsx) in placedLevels)
        {
            var name = Path.GetFileNameWithoutExtension(ddmFile);
            try
            {
                var result = MeshExportCliOptions.ExportFile(
                    ddmFile,
                    output,
                    ModelSourceKind.Ddm,
                    format,
                    blenderHelperPath,
                    cancellationToken,
                    name,
                    hasPlacedPsxCompanion: true,
                    ddxPath: ddxPath,
                    psxPath: psxDir ?? levelPsx,
                    ddmTexturePath: texturePath);
                totalTriangles += result.Triangles;
                converted++;
                if (verbose)
                    AnsiConsole.MarkupLine($"  {name}: [green]{result.Triangles:N0} triangles[/]");
            }
            catch (Exception ex)
            {
                failed++;
                if (verbose)
                    AnsiConsole.MarkupLine($"  {name}: [red]error: {ex.Message.EscapeMarkup()}[/]");
            }
        }

        foreach (var file in standaloneDdmFiles)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            try
            {
                var result = MeshExportCliOptions.ExportFile(
                    file,
                    output,
                    ModelSourceKind.Ddm,
                    format,
                    blenderHelperPath,
                    cancellationToken,
                    name,
                    ddxPath: ddxPath,
                    ddmTexturePath: texturePath);
                totalTriangles += result.Triangles;
                converted++;
                if (verbose)
                    AnsiConsole.MarkupLine($"  {name}: [green]{result.Triangles:N0} triangles[/]");
            }
            catch (Exception ex)
            {
                failed++;
                if (verbose)
                    AnsiConsole.MarkupLine($"  {name}: [red]error: {ex.Message.EscapeMarkup()}[/]");
            }
        }

        var total = standaloneDdmFiles.Count + placedLevels.Count;
        AnsiConsole.MarkupLine(
            $"Converted [green]{converted}[/]/{total} files " +
            $"({totalTriangles:N0} triangles)" +
            (failed > 0 ? $", [red]{failed} failed[/]" : ""));
        return failed > 0 ? 1 : 0;
    }

    /// <summary>
    ///     Auto-detects a companion directory: checks input dir for matching files, then sibling dir.
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
    ///     Classifies DDM files into placed levels (have PSX companion) and object companions (_o suffix).
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
