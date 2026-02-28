using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Ps2Scene;
using NeversoftMultitool.Core.Formats.Psx;
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
        var texturesOption = new Option<bool>("-t", "--textures")
        {
            Description = "Embed textures from companion .tex/.tex.ps2 files"
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
        command.Options.Add(texturesOption);
        command.Options.Add(texPathOption);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var textures = parseResult.GetValue(texturesOption);
            var texPath = parseResult.GetValue(texPathOption);
            var verbose = parseResult.GetValue(verboseOption);

            return Task.FromResult(Execute(input, output, textures, texPath, verbose));
        });

        return command;
    }

    private static int Execute(string input, string output, bool embedTextures,
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
        Ps2SceneGltfWriter.Tex0Resolver? tex0Resolver = null;
        Dictionary<uint, Ps2Texture>? textureCache = null;

        if (embedTextures || texPath != null)
        {
            textureCache = BuildTextureCache(files, texPath, verbose);
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
            var vramMapping = BuildVramMapping(files, texPath, verbose);
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
                if (provider == null && embedTextures)
                {
                    var perFileCache = TryLoadCompanionTex(file, stem);
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

                var tris = Ps2SceneGltfWriter.Write(scene, outputPath, provider, tex0Resolver);
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

    private static Dictionary<uint, Ps2Texture> BuildTextureCache(
        List<string> geomFiles, string? texPath, bool verbose)
    {
        var cache = new Dictionary<uint, Ps2Texture>();
        var parsedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (texPath != null)
        {
            foreach (var tf in GetTexFiles(texPath))
                ParseTexIntoCache(tf, cache, parsedFiles, verbose);
            return cache;
        }

        // Auto-detect: scan from common root of all GEOM files
        var commonRoot = CompanionSearch.GetCommonRoot(geomFiles);
        if (commonRoot != null)
        {
            var texFiles = CompanionSearch.FindAllByExtension(
                commonRoot, [".tex.ps2", ".tex"]);
            foreach (var tf in texFiles)
                ParseTexIntoCache(tf, cache, parsedFiles, verbose);
        }

        return cache;
    }

    private static Dictionary<uint, Ps2Texture>? TryLoadCompanionTex(string geomFile, string stem)
    {
        var dir = Path.GetDirectoryName(geomFile);
        if (dir == null) return null;

        var texFile = CompanionSearch.FindCompanion(
            dir, stem, [".tex.ps2", ".tex"], ["TEX", "Textures"]);
        if (texFile == null) return null;

        try
        {
            var result = Ps2TexFile.Parse(texFile);
            if (!result.Success) return null;

            var perCache = new Dictionary<uint, Ps2Texture>();
            foreach (var tex in result.Textures)
            {
                if (tex.Pixels != null)
                    perCache.TryAdd(tex.Checksum, tex);
            }
            return perCache;
        }
        catch
        {
            return null;
        }
    }

    private static void ParseTexIntoCache(string texFile,
        Dictionary<uint, Ps2Texture> cache, HashSet<string> parsedFiles, bool verbose)
    {
        if (!parsedFiles.Add(texFile)) return;

        try
        {
            var result = Ps2TexFile.Parse(texFile);
            if (!result.Success) return;

            foreach (var tex in result.Textures)
            {
                if (tex.Pixels != null)
                    cache.TryAdd(tex.Checksum, tex);
            }
        }
        catch (Exception ex)
        {
            if (verbose)
            {
                AnsiConsole.MarkupLine(
                    $"  TEX {Path.GetFileName(texFile)}: [yellow]{ex.Message.EscapeMarkup()}[/]");
            }
        }
    }

    /// <summary>
    /// Builds a VRAM (GroupChecksum,TBP,CBP)→checksum mapping from all TEX files.
    /// Used for THPS4 GEOM texture resolution where CGeomNode.texture_checksum is 0
    /// and texture references are encoded as TEX0_1 GS register values in the DMA chain.
    /// Group-aware keying prevents collisions from double-buffered VRAM banks.
    /// </summary>
    private static Dictionary<(uint, uint, uint), uint> BuildVramMapping(
        List<string> geomFiles, string? texPath, bool verbose)
    {
        var mapping = new Dictionary<(uint, uint, uint), uint>();
        var parsedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (texPath != null)
        {
            foreach (var tf in GetTexFiles(texPath))
                MergeVramMapping(tf, mapping, parsedFiles, verbose);
            return mapping;
        }

        // Auto-detect: scan from common root of all GEOM files
        var commonRoot = CompanionSearch.GetCommonRoot(geomFiles);
        if (commonRoot != null)
        {
            var texFiles = CompanionSearch.FindAllByExtension(
                commonRoot, [".tex.ps2", ".tex"]);
            foreach (var tf in texFiles)
                MergeVramMapping(tf, mapping, parsedFiles, verbose);
        }

        return mapping;
    }

    private static void MergeVramMapping(string texFile,
        Dictionary<(uint, uint, uint), uint> mapping, HashSet<string> parsedFiles, bool verbose)
    {
        if (!parsedFiles.Add(texFile)) return;

        try
        {
            var fileMapping = Ps2VramAllocator.BuildMapping(texFile);
            foreach (var (key, checksum) in fileMapping)
                mapping.TryAdd(key, checksum);
        }
        catch (Exception ex)
        {
            if (verbose)
            {
                AnsiConsole.MarkupLine(
                    $"  VRAM {Path.GetFileName(texFile)}: [yellow]{ex.Message.EscapeMarkup()}[/]");
            }
        }
    }

    private static List<string> GetTexFiles(string path)
    {
        if (File.Exists(path))
            return [path];

        if (!Directory.Exists(path))
            return [];

        return Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return name.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".tex.ps2", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();
    }
}
