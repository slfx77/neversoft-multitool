using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Texture;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class Ps2GeomCommand
{
    private const string Extension = ".geom.ps2";

    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a PS2 GEOM file (.geom.ps2) or directory"
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

        var command = new Command("ps2geom", "Convert PS2 GEOM files (level geometry) to glTF (.glb)");
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
                .Where(f => f.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Path not found: {input}");
            return 1;
        }

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No PS2 GEOM files found.[/]");
            return 0;
        }

        // Build texture lookup if requested
        Ps2SceneGltfWriter.TextureProvider? textureProvider = null;
        Ps2GeomGltfWriter.Tex0Resolver? tex0Resolver = null;
        Dictionary<uint, Ps2Texture>? textureCache = null;

        textureCache = Ps2TextureLoader.BuildTextureCache(files, texPath, verbose);
        if (textureCache.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"Loaded [green]{textureCache.Count}[/] textures for embedding");
            textureProvider = checksum =>
            {
                if (!textureCache.TryGetValue(checksum, out var tex) || tex.Pixels == null)
                    return null;
                return ImageWriter.WritePngToMemory(tex.Width, tex.Height, tex.Pixels);
            };
        }

        // Build VRAM mapping for THPS4 (where CGeomNode.texture_checksum is always 0)
        var vramMapping = Ps2TextureLoader.BuildTex0Mapping(files, texPath, verbose);
        if (vramMapping.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"Built VRAM mapping with [green]{vramMapping.Count}[/] entries for TEX0 lookup");
            tex0Resolver = (dmaTex0, groupChecksum) =>
            {
                var key = Ps2VramAllocator.DecodeTex0Key(dmaTex0, groupChecksum);
                return vramMapping.GetValueOrDefault(key);
            };
        }

        Directory.CreateDirectory(output);
        AnsiConsole.MarkupLine($"Found [green]{files.Count}[/] PS2 GEOM file(s)");

        var stopwatch = Stopwatch.StartNew();
        var converted = 0;
        var failed = 0;
        var skipped = 0;
        var totalTriangles = 0;
        var texturedCount = 0;

        foreach (var file in files)
        {
            var filename = Path.GetFileName(file);
            var stem = filename;
            if (stem.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
                stem = stem[..^Extension.Length];

            var outputPath = Path.Combine(output, stem + ".glb");

            try
            {
                var scene = Ps2GeomFile.Parse(file);

                // Try per-file texture provider
                var provider = textureProvider;
                if (provider == null)
                {
                    var perFileCache = Ps2TextureLoader.TryLoadCompanionTex(file, stem);
                    if (perFileCache != null && perFileCache.Count > 0)
                    {
                        provider = checksum =>
                        {
                            if (!perFileCache.TryGetValue(checksum, out var tex) || tex.Pixels == null)
                                return null;
                            return ImageWriter.WritePngToMemory(tex.Width, tex.Height, tex.Pixels);
                        };
                    }
                }

                var tris = Ps2GeomGltfWriter.Write(scene, outputPath, provider, tex0Resolver);
                if (tris == 0)
                {
                    skipped++;
                    if (verbose)
                        AnsiConsole.MarkupLine($"  {filename}: [yellow]empty (0 triangles)[/]");
                    continue;
                }

                totalTriangles += tris;
                converted++;

                if (provider != null)
                    texturedCount++;

                if (verbose)
                {
                    var texInfo = provider != null ? ", textured" : "";
                    AnsiConsole.MarkupLine(
                        $"  {filename}: [green]{scene.Leaves.Count} leaves, " +
                        $"{tris:N0} triangles{texInfo}[/]");
                }
            }
            catch (Exception ex)
            {
                failed++;
                if (verbose)
                    AnsiConsole.MarkupLine($"  {filename}: [red]{ex.Message.EscapeMarkup()}[/]");
            }
        }

        stopwatch.Stop();
        var texMsg = texturedCount > 0 ? $", {texturedCount} textured" : "";
        var skipMsg = skipped > 0 ? $", {skipped} empty" : "";
        AnsiConsole.MarkupLine(
            $"Converted [green]{converted}[/]/{files.Count} files " +
            $"({totalTriangles:N0} triangles, {failed} failed{skipMsg}{texMsg}) " +
            $"in {stopwatch.Elapsed.TotalSeconds:F2}s");

        return 0;
    }
}
