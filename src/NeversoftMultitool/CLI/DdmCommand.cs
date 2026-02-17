using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Psx;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class DdmCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to directory containing .ddm files"
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

        var command = new Command("ddm", "Convert DDM mesh files to glTF (.glb)");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(texturesOption);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var textures = parseResult.GetValue(texturesOption);
            var verbose = parseResult.GetValue(verboseOption);

            return Task.FromResult(Execute(input, output, textures, verbose));
        });

        return command;
    }

    private static int Execute(string input, string output, string? textures, bool verbose)
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

        Directory.CreateDirectory(output);

        var textureInfo = textures != null && Directory.Exists(textures)
            ? $", textures={textures}"
            : "";
        AnsiConsole.MarkupLine($"Found [green]{ddmFiles.Length}[/] DDM file(s){textureInfo}");

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
            var result = ConvertDdmFile(file, output, textures, verbose);
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
        string file, string output, string? textures, bool verbose)
    {
        var filename = Path.GetFileName(file);
        var ddmName = Path.GetFileNameWithoutExtension(filename);
        var inputDir = Path.GetDirectoryName(file)!;

        try
        {
            var ddm = DdmFile.Parse(file);

            // Auto-detect PSX file for world placement
            var psxFile = FindCompanionFile(inputDir, ddmName, ".psx");
            var positions = psxFile != null
                ? PsxObjectPositionParser.ParsePositions(psxFile)
                : null;

            if (positions != null)
                return ConvertPlacedLevel(ddm, ddmName, inputDir, output, textures, positions, verbose);

            return ConvertStandalone(ddm, ddmName, filename, output, textures, verbose);
        }
        catch (Exception ex)
        {
            if (verbose)
                AnsiConsole.MarkupLine($"  {filename}: [red]error: {ex.Message}[/]");
            return new ConvertResult(0, 0, false, false);
        }
    }

    private static ConvertResult ConvertPlacedLevel(
        DdmFile ddm, string ddmName, string inputDir, string output,
        string? textures, List<PsxObjectPosition> positions, bool verbose)
    {
        var objectsFile = FindCompanionFile(inputDir, ddmName + "_o", ".ddm");
        var objectsDdm = objectsFile != null ? DdmFile.Parse(objectsFile) : null;

        var result = GltfWriter.WritePlacedLevel(
            ddm, objectsDdm, positions, output, ddmName, textures);

        var objects = ddm.Objects.Count + (objectsDdm?.Objects.Count ?? 0);
        var triangles = result.Level + result.Objects;

        if (verbose)
        {
            var matched = positions.Count(p => p.MeshIndex < ddm.Objects.Count);
            AnsiConsole.MarkupLine(
                $"  {ddmName}: [green]placed level[/] " +
                $"({ddm.Objects.Count} meshes, {matched}/{positions.Count} positions matched, " +
                $"{result.Level:N0} level tris, {result.Objects:N0} object tris)");
        }

        return new ConvertResult(objects, triangles, true, true);
    }

    private static ConvertResult ConvertStandalone(
        DdmFile ddm, string ddmName, string filename, string output,
        string? textures, bool verbose)
    {
        var outputFile = Path.Combine(output, ddmName + ".glb");
        var triangles = GltfWriter.WriteDdm(ddm, outputFile, textures, ddmName);

        if (verbose)
        {
            AnsiConsole.MarkupLine(
                $"  {filename}: [green]{ddm.Objects.Count} objects, {triangles:N0} triangles[/]");
        }

        return new ConvertResult(ddm.Objects.Count, triangles, true, false);
    }

    private static string? FindCompanionFile(string directory, string stem, string extension)
    {
        var files = Directory.GetFiles(directory, stem + extension,
            new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive });
        return files.Length > 0 ? files[0] : null;
    }
}
