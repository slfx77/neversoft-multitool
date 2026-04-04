using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;
using NeversoftMultitool.Core.Formats.Texture.RenderWare;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class RwBspCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a RenderWare BSP file (.bsp) or directory"
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

        var command = new Command("rwbsp", "Convert RenderWare BSP level files to glTF (.glb)");
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
            files = Directory.GetFiles(input, "*.bsp", SearchOption.AllDirectories)
                .ToList();
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Path not found: {input}");
            return 1;
        }

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No BSP files found.[/]");
            return 0;
        }

        // Probe for unsupported files
        var (supported, unsupported) = FormatProbe.PartitionFiles(files, FormatProbe.ProbeMesh);
        if (unsupported.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"Found [green]{files.Count}[/] files " +
                $"([green]{supported.Count}[/] supported, [yellow]{unsupported.Count}[/] unsupported)");
            foreach (var (fileName, reason) in unsupported)
                AnsiConsole.MarkupLine($"  [yellow]\u26a0[/] {Markup.Escape(fileName)}: {Markup.Escape(reason)}");
            files = supported;
        }

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No supported BSP files to process.[/]");
            return 0;
        }

        Directory.CreateDirectory(output);
        AnsiConsole.MarkupLine($"Found [green]{files.Count}[/] BSP file(s)");

        // Build texture cache if requested
        var textureCaches = new Dictionary<string, RwDffGltfWriter.TextureProvider>(
            StringComparer.OrdinalIgnoreCase);

        var stopwatch = Stopwatch.StartNew();
        var totalTriangles = 0;
        var converted = 0;
        var failed = 0;
        var texturedCount = 0;

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            var stem = Path.GetFileNameWithoutExtension(file);

            try
            {
                var data = File.ReadAllBytes(file);
                if (!RwBspFile.IsBspFile(data))
                {
                    if (verbose)
                        AnsiConsole.MarkupLine($"  {fileName}: [yellow]Not a BSP file[/]");
                    continue;
                }

                var world = RwBspFile.Parse(data);

                // Resolve texture provider
                RwDffGltfWriter.TextureProvider? textureProvider = null;
                textureProvider = GetTextureProvider(file, texPath, textureCaches, verbose);
                if (textureProvider != null)
                    texturedCount++;

                var outputFile = Path.Combine(output, stem + ".glb");
                var triangles = RwBspGltfWriter.Write(world, outputFile, textureProvider);

                totalTriangles += triangles;
                converted++;

                if (verbose)
                {
                    var texInfo = textureProvider != null ? ", textured" : "";
                    AnsiConsole.MarkupLine(
                        $"  {fileName}: [green]{triangles:N0}[/] tris, " +
                        $"{world.Sections.Length} sectors, {world.Materials.Length} mat{texInfo}");
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
        var texMsg = texturedCount > 0 ? $", {texturedCount} textured" : "";
        AnsiConsole.MarkupLine(
            $"\nConverted [green]{converted}[/] files ({totalTriangles:N0} triangles{texMsg}) " +
            $"in {stopwatch.Elapsed.TotalSeconds:F2}s" +
            (failed > 0 ? $", [red]{failed} failed[/]" : ""));

        return failed > 0 ? 1 : 0;
    }

    private static RwDffGltfWriter.TextureProvider? GetTextureProvider(
        string bspFile, string? texPath,
        Dictionary<string, RwDffGltfWriter.TextureProvider> cache, bool verbose)
    {
        string? texFile;
        if (texPath != null)
        {
            if (File.Exists(texPath))
            {
                texFile = texPath;
            }
            else if (Directory.Exists(texPath))
            {
                var stem = Path.GetFileNameWithoutExtension(bspFile);
                texFile = FindTexWithFallback(texPath, stem);
            }
            else
            {
                return null;
            }
        }
        else
        {
            var dir = Path.GetDirectoryName(bspFile);
            if (dir == null) return null;
            var stem = Path.GetFileNameWithoutExtension(bspFile);
            texFile = FindTexWithFallback(dir, stem);
        }

        if (texFile == null) return null;

        if (cache.TryGetValue(texFile, out var existing))
            return existing;

        try
        {
            var txdResult = RwTxdFile.Parse(texFile);
            if (!txdResult.Success)
            {
                if (verbose)
                    AnsiConsole.MarkupLine($"  TEX {Path.GetFileName(texFile)}: [yellow]Parse failed[/]");
                return null;
            }

            var provider = RwBspGltfWriter.BuildTxdTextureProvider(txdResult);
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

    /// <summary>
    ///     Finds a .tex companion file, with fallback: if "Ware_Ware.tex" isn't found,
    ///     tries stripping the last underscore suffix to find "Ware.tex".
    ///     Handles BSP naming where the detail level (Ware_Ware.bsp) shares
    ///     textures with the base name (Ware.tex).
    /// </summary>
    private static string? FindTexWithFallback(string searchDir, string stem)
    {
        var texFile = CompanionSearch.FindCompanion(searchDir, stem, [".tex"], ["TEX", "Textures"]);
        if (texFile != null)
            return texFile;

        // Fallback: strip last _suffix and retry (e.g. Ware_Ware → Ware)
        var lastUnderscore = stem.LastIndexOf('_');
        if (lastUnderscore > 0)
            return CompanionSearch.FindCompanion(searchDir, stem[..lastUnderscore], [".tex"], ["TEX", "Textures"]);

        return null;
    }
}
