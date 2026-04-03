using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Psx;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class RwDffCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a RenderWare DFF file (.SKN) or directory"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for .glb files",
            DefaultValueFactory = _ => "TestOutput"
        };
        var texPathOption = new Option<string?>("--tex")
        {
            Description = "Explicit TEX file or directory to use for texture lookup"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };

        var command = new Command("rwdff", "Convert RenderWare DFF mesh files (.SKN) to glTF (.glb)");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(texPathOption);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var texPath = parseResult.GetValue(texPathOption);
            var verbose = parseResult.GetValue(verboseOption);

            return Task.FromResult(Execute(input, output, texPath, verbose));
        });

        return command;
    }

    private static int Execute(string input, string output,
        string? texPath, bool verbose)
    {
        List<string> files;

        if (File.Exists(input))
        {
            files = [input];
        }
        else if (Directory.Exists(input))
        {
            files = Directory.GetFiles(input, "*.*", SearchOption.AllDirectories)
                .Where(IsDffFile)
                .ToList();
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Path not found: {input}");
            return 1;
        }

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No RW DFF (.SKN) files found.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"Found [green]{files.Count}[/] DFF file(s)");

        // Build texture cache if requested
        var textureCaches = new Dictionary<string, RwDffGltfWriter.TextureProvider>(
            StringComparer.OrdinalIgnoreCase);

        var stopwatch = Stopwatch.StartNew();
        var totalTriangles = 0;
        var converted = 0;
        var failed = 0;

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            var stem = Path.GetFileNameWithoutExtension(file);

            try
            {
                var data = File.ReadAllBytes(file);
                if (!RwDffFile.IsDffFile(data))
                {
                    if (verbose)
                        AnsiConsole.MarkupLine($"  {fileName}: [yellow]Not a DFF file[/]");
                    continue;
                }

                var clump = RwDffFile.Parse(data);

                // Resolve texture provider for this file
                RwDffGltfWriter.TextureProvider? textureProvider = null;
                textureProvider = GetTextureProvider(file, texPath, textureCaches, verbose);

                var outputFile = Path.Combine(output, stem + ".glb");
                var triangles = RwDffGltfWriter.Write(clump, outputFile, textureProvider);

                totalTriangles += triangles;
                converted++;

                if (verbose)
                {
                    var geomCount = clump.Geometries.Length;
                    var matCount = clump.Geometries.Sum(g => g.Materials.Length);
                    AnsiConsole.MarkupLine(
                        $"  {fileName}: [green]{triangles:N0}[/] tris, " +
                        $"{geomCount} geom, {matCount} mat");
                }
            }
            catch (Exception ex)
            {
                failed++;
                if (verbose)
                    AnsiConsole.WriteException(ex);
                AnsiConsole.MarkupLine(
                    $"  {fileName}: [red]{ex.Message.EscapeMarkup()}[/]");
            }
        }

        stopwatch.Stop();
        AnsiConsole.MarkupLine(
            $"\nConverted [green]{converted}[/] files ({totalTriangles:N0} triangles) " +
            $"in {stopwatch.Elapsed.TotalSeconds:F2}s" +
            (failed > 0 ? $", [red]{failed} failed[/]" : ""));

        return failed > 0 ? 1 : 0;
    }

    private static RwDffGltfWriter.TextureProvider? GetTextureProvider(
        string dffFile, string? texPath,
        Dictionary<string, RwDffGltfWriter.TextureProvider> cache, bool verbose)
    {
        // Determine which TEX file to use
        string? texFile;
        if (texPath != null)
        {
            // Explicit path: use it directly or find matching file in directory
            if (File.Exists(texPath))
            {
                texFile = texPath;
            }
            else if (Directory.Exists(texPath))
            {
                var stem = Path.GetFileNameWithoutExtension(dffFile);
                texFile = CompanionSearch.FindCompanion(texPath, stem, [".tex"], ["TEX", "Textures"]);
            }
            else
            {
                return null;
            }
        }
        else
        {
            // Auto-discover companion .tex file
            var dir = Path.GetDirectoryName(dffFile);
            if (dir == null) return null;
            var stem = Path.GetFileNameWithoutExtension(dffFile);
            texFile = CompanionSearch.FindCompanion(dir, stem, [".tex"], ["TEX", "Textures"]);
        }

        if (texFile == null) return null;

        // Check cache
        if (cache.TryGetValue(texFile, out var existing))
            return existing;

        // Parse the TXD file
        try
        {
            var txdResult = RwTxdFile.Parse(texFile);
            if (!txdResult.Success)
            {
                if (verbose)
                    AnsiConsole.MarkupLine($"  TEX {Path.GetFileName(texFile)}: [yellow]Parse failed[/]");
                return null;
            }

            var provider = RwDffGltfWriter.BuildTxdTextureProvider(txdResult);
            cache[texFile] = provider;

            if (verbose)
                AnsiConsole.MarkupLine(
                    $"  Loaded [green]{txdResult.Textures.Count}[/] textures from {Path.GetFileName(texFile)}");

            return provider;
        }
        catch (Exception ex)
        {
            if (verbose)
                AnsiConsole.MarkupLine(
                    $"  TEX {Path.GetFileName(texFile)}: [yellow]{ex.Message.EscapeMarkup()}[/]");
            return null;
        }
    }

    private static bool IsDffFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".SKN", StringComparison.OrdinalIgnoreCase);
    }
}
