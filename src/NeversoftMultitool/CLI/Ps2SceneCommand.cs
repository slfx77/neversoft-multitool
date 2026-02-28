using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Ps2Scene;
using NeversoftMultitool.Core.Formats.Psx;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class Ps2SceneCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a PS2 scene file (.mdl.ps2, .skin.ps2, .iskin.ps2) or directory"
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
        var skeletonOption = new Option<string?>("--ske")
        {
            Description = "Skeleton file (.ske.ps2) or directory. Auto-discovered for .skin.ps2 files if not specified."
        };

        var command = new Command("ps2scene", "Convert PS2 scene files (MDL/SKIN) to glTF (.glb)");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(texturesOption);
        command.Options.Add(texPathOption);
        command.Options.Add(verboseOption);
        command.Options.Add(skeletonOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var textures = parseResult.GetValue(texturesOption);
            var texPath = parseResult.GetValue(texPathOption);
            var verbose = parseResult.GetValue(verboseOption);
            var skePath = parseResult.GetValue(skeletonOption);

            return Task.FromResult(Execute(input, output, textures, texPath, verbose, skePath));
        });

        return command;
    }

    private static int Execute(string input, string output, bool embedTextures,
        string? texPath, bool verbose, string? skePath = null)
    {
        List<string> files;

        if (File.Exists(input))
        {
            files = [input];
        }
        else if (Directory.Exists(input))
        {
            files = Directory.GetFiles(input, "*.*", SearchOption.AllDirectories)
                .Where(IsPs2SceneFile)
                .ToList();
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Path not found: {input}");
            return 1;
        }

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No PS2 scene files found.[/]");
            return 0;
        }

        // Build texture lookup if requested
        Ps2SceneGltfWriter.TextureProvider? textureProvider = null;
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
        }

        Directory.CreateDirectory(output);
        AnsiConsole.MarkupLine($"Found [green]{files.Count}[/] PS2 scene file(s)");

        // Pre-load explicit skeleton if provided
        Ps2Skeleton? explicitSkeleton = null;
        Dictionary<string, Ps2Skeleton>? skeletonCache = null;
        if (skePath != null)
        {
            if (File.Exists(skePath))
            {
                explicitSkeleton = Ps2SkeletonFile.Parse(skePath);
                AnsiConsole.MarkupLine(
                    $"Loaded skeleton: [green]{explicitSkeleton.Bones.Length} bones[/]");
            }
            else if (Directory.Exists(skePath))
            {
                skeletonCache = new Dictionary<string, Ps2Skeleton>(StringComparer.OrdinalIgnoreCase);
                foreach (var skeFile in Directory.GetFiles(skePath, "*.ske.ps2"))
                {
                    var skeStem = Path.GetFileName(skeFile).Replace(".ske.ps2", "", StringComparison.OrdinalIgnoreCase);
                    try
                    {
                        skeletonCache[skeStem] = Ps2SkeletonFile.Parse(skeFile);
                    }
                    catch
                    {
                        // Skip unparseable skeleton files
                    }
                }
                if (skeletonCache.Count > 0)
                    AnsiConsole.MarkupLine($"Loaded [green]{skeletonCache.Count}[/] skeletons from directory");
            }
        }

        var stopwatch = Stopwatch.StartNew();
        var converted = 0;
        var failed = 0;
        var skipped = 0;
        var totalTriangles = 0;
        var texturedCount = 0;
        var skinnedCount = 0;

        foreach (var file in files)
        {
            var filename = Path.GetFileName(file);
            // Strip compound extensions: foo.mdl.ps2 → foo
            var stem = filename;
            foreach (var ext in Ps2SceneFile.SupportedExtensions)
            {
                if (stem.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                {
                    stem = stem[..^ext.Length];
                    break;
                }
            }

            var outputPath = Path.Combine(output, stem + ".glb");

            try
            {
                // Detect THUG2 pre-compiled DMA .skin.ps2 files before parsing
                var fileData = File.ReadAllBytes(file);
                if (fileData.Length >= 4 && BitConverter.ToInt32(fileData, 0) == 1
                    && filename.EndsWith(".skin.ps2", StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    if (verbose)
                    {
                        var iskinFile = file.Replace(".skin.ps2", ".iskin.ps2",
                            StringComparison.OrdinalIgnoreCase);
                        var iskinHint = File.Exists(iskinFile)
                            ? " (matching .iskin.ps2 exists)"
                            : "";
                        AnsiConsole.MarkupLine(
                            $"  {filename}: [yellow]pre-compiled VIF/DMA format, skipped{iskinHint}[/]");
                    }
                    continue;
                }

                var scene = Ps2SceneFile.Parse(fileData);

                // Use per-file texture provider if we have a cache,
                // or try auto-detecting a companion TEX file for this specific scene
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

                // Resolve skeleton: explicit > cache > auto-discover
                var skeleton = explicitSkeleton;
                if (skeleton == null && skeletonCache != null)
                    skeletonCache.TryGetValue(stem, out skeleton);
                if (skeleton == null && filename.Contains(".skin.", StringComparison.OrdinalIgnoreCase))
                    skeleton = TryDiscoverSkeleton(file, stem);

                int tris;
                if (skeleton != null)
                {
                    tris = Ps2SceneGltfWriter.WriteSkinned(scene, skeleton, outputPath, provider);
                    if (tris > 0) skinnedCount++;
                }
                else
                {
                    tris = Ps2SceneGltfWriter.Write(scene, outputPath, provider);
                }

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
                    var skelInfo = skeleton != null ? $", {skeleton.Bones.Length} bones" : "";
                    AnsiConsole.MarkupLine(
                        $"  {filename}: [green]{scene.MeshGroups.Count} groups, " +
                        $"{tris:N0} triangles{texInfo}{skelInfo}[/]");
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
        var skelMsg = skinnedCount > 0 ? $", {skinnedCount} skinned" : "";
        var skipMsg = skipped > 0 ? $", {skipped} empty" : "";
        AnsiConsole.MarkupLine(
            $"Converted [green]{converted}[/]/{files.Count} files " +
            $"({totalTriangles:N0} triangles, {failed} failed{skipMsg}{texMsg}{skelMsg}) " +
            $"in {stopwatch.Elapsed.TotalSeconds:F2}s");

        return 0;
    }

    /// <summary>
    /// Builds a combined texture cache from an explicit TEX path or by scanning
    /// from the common root directory of all scene files.
    /// </summary>
    private static Dictionary<uint, Ps2Texture> BuildTextureCache(
        List<string> sceneFiles, string? texPath, bool verbose)
    {
        var cache = new Dictionary<uint, Ps2Texture>();
        var parsedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // If explicit path provided, parse it
        if (texPath != null)
        {
            var texFiles = GetTexFiles(texPath);
            foreach (var tf in texFiles)
                ParseTexIntoCache(tf, cache, parsedFiles, verbose);
            return cache;
        }

        // Auto-detect: scan from common root of all scene files
        var commonRoot = CompanionSearch.GetCommonRoot(sceneFiles);
        if (commonRoot != null)
        {
            var texFiles = CompanionSearch.FindAllByExtension(
                commonRoot, [".tex.ps2", ".tex"]);
            foreach (var tf in texFiles)
                ParseTexIntoCache(tf, cache, parsedFiles, verbose);
        }

        return cache;
    }

    /// <summary>
    /// Try to load a companion TEX file for a specific scene file.
    /// Searches: same directory → sibling TEX/ → ancestor walk (Textures/, TEX/).
    /// </summary>
    private static Dictionary<uint, Ps2Texture>? TryLoadCompanionTex(string sceneFile, string stem)
    {
        var dir = Path.GetDirectoryName(sceneFile);
        if (dir == null) return null;

        var texFile = CompanionSearch.FindCompanion(
            dir, stem, [".tex.ps2", ".tex"], ["TEX", "Textures"]);
        if (texFile == null) return null;

        try
        {
            var result = Ps2TexFile.Parse(texFile);
            if (!result.Success) return null;

            var cache = new Dictionary<uint, Ps2Texture>();
            foreach (var tex in result.Textures)
            {
                if (tex.Pixels != null)
                    cache.TryAdd(tex.Checksum, tex);
            }
            return cache;
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

    /// <summary>
    /// Auto-discover a companion skeleton file for a .skin.ps2 file.
    /// Searches: same directory → sibling SKE/ → ancestor walk (Skeletons/, SKE/).
    /// Tries .ske.ps2 first, then .ske (THPS4 uses .ske without .ps2 suffix).
    /// </summary>
    private static Ps2Skeleton? TryDiscoverSkeleton(string skinFile, string stem)
    {
        var dir = Path.GetDirectoryName(skinFile);
        if (dir == null) return null;

        var skeFile = CompanionSearch.FindCompanion(
            dir, stem, [".ske.ps2", ".ske"], ["SKE", "Skeletons"]);
        if (skeFile == null) return null;

        try
        {
            return Ps2SkeletonFile.Parse(skeFile);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsPs2SceneFile(string path)
    {
        var name = Path.GetFileName(path);
        return Ps2SceneFile.SupportedExtensions
            .Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }
}
