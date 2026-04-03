using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Psx;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class PsxMeshCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a PSX file or directory containing PSX files"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for glTF (.glb) files",
            DefaultValueFactory = _ => "TestOutput/PsxMesh"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };

        var command = new Command("psx-mesh", "Convert PSX model files to glTF (.glb)");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var verbose = parseResult.GetValue(verboseOption);

            return Task.FromResult(Execute(input, output, verbose));
        });

        return command;
    }

    private static int Execute(string input, string output, bool verbose)
    {
        List<string> psxFiles;

        if (File.Exists(input))
        {
            psxFiles = [input];
        }
        else if (Directory.Exists(input))
        {
            psxFiles = Directory.GetFiles(input, "*.psx",
                    new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
                .ToList();
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Path not found: {input}");
            return 1;
        }

        if (psxFiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No .psx files found.[/]");
            return 0;
        }

        Directory.CreateDirectory(output);
        AnsiConsole.MarkupLine($"Found [green]{psxFiles.Count}[/] PSX file(s)");

        var stopwatch = Stopwatch.StartNew();
        var converted = 0;
        var skipped = 0;
        var totalMeshes = 0;
        var totalTriangles = 0;

        foreach (var file in psxFiles)
        {
            var filename = Path.GetFileName(file);
            var stem = Path.GetFileNameWithoutExtension(filename);

            try
            {
                var psxFile = PsxMeshFile.Parse(file);
                if (psxFile == null)
                {
                    skipped++;
                    if (verbose)
                        AnsiConsole.MarkupLine($"  {filename}: [grey]skipped (no mesh data)[/]");
                    continue;
                }

                // For level geometry files (*_g.psx), textures are in
                // the companion library file (*_l.psx)
                string? companionLibPath = null;
                if (stem.EndsWith("_g", StringComparison.OrdinalIgnoreCase))
                {
                    var libStem = stem[..^2] + "_l";
                    var dir = Path.GetDirectoryName(file)!;
                    var candidates = Directory.GetFiles(dir, libStem + ".psx",
                        new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive });
                    if (candidates.Length > 0)
                        companionLibPath = candidates[0];
                }

                PsxGltfWriter.TextureProvider textureProvider = hash =>
                {
                    var result = PsxLibrary.ExtractTextureByHash(file, hash);
                    if (result == null && companionLibPath != null)
                        result = PsxLibrary.ExtractTextureByHash(companionLibPath, hash);
                    if (result == null) return null;
                    var (rgba, w, h) = result.Value;
                    return ImageWriter.WritePngToMemory(w, h, rgba);
                };

                // Auto-detect companion PSH for bone names in character models
                var pshFile = psxFile.HasHierarchy ? PshFile.FindCompanion(file) : null;

                var outputFile = Path.Combine(output, stem + ".glb");
                var triangles = PsxGltfWriter.Write(psxFile, outputFile, textureProvider, pshFile);

                totalMeshes += psxFile.Meshes.Count;
                totalTriangles += triangles;
                converted++;

                if (verbose)
                {
                    AnsiConsole.MarkupLine(
                        $"  {filename}: [green]v{psxFile.Version:X} " +
                        $"{psxFile.Objects.Count} objects, {psxFile.Meshes.Count} meshes, " +
                        $"{triangles:N0} triangles[/]");
                }
            }
            catch (Exception ex)
            {
                if (verbose)
                    AnsiConsole.MarkupLine($"  {Markup.Escape(filename)}: [red]error: {Markup.Escape(ex.Message)}[/]");
            }
        }

        stopwatch.Stop();
        AnsiConsole.MarkupLine(
            $"Converted [green]{converted}[/]/{psxFiles.Count} files " +
            $"({skipped} texture-only, {totalMeshes:N0} meshes, {totalTriangles:N0} triangles) " +
            $"in {stopwatch.Elapsed.TotalSeconds:F2}s");

        return 0;
    }
}
