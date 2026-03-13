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
            Description = "Skeleton file (.ske.ps2 or .ske) or directory. Auto-discovered for .skin.ps2 files if not specified."
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

        // Probe for unsupported files (THAW .skin.ps2, Xbox/PC scene files)
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
            AnsiConsole.MarkupLine("[yellow]No supported PS2 scene files to process.[/]");
            return 0;
        }

        // Build texture lookup if requested
        Ps2SceneGltfWriter.TextureProvider? textureProvider = null;
        Dictionary<uint, Ps2Texture>? textureCache = null;

        if (embedTextures || texPath != null)
        {
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
                explicitSkeleton = ParseSkeletonFile(skePath);
                AnsiConsole.MarkupLine(
                    $"Loaded skeleton: [green]{explicitSkeleton.Bones.Length} bones[/]");
            }
            else if (Directory.Exists(skePath))
            {
                skeletonCache = new Dictionary<string, Ps2Skeleton>(StringComparer.OrdinalIgnoreCase);

                // Load .ske.ps2 files (PS2-specific format)
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

                // Load .ske files (cross-platform format, used by THPS4)
                // Only add if no .ske.ps2 already loaded for same stem
                foreach (var skeFile in Directory.GetFiles(skePath, "*.ske"))
                {
                    if (skeFile.EndsWith(".ske.ps2", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var skeStem = Path.GetFileName(skeFile).Replace(".ske", "", StringComparison.OrdinalIgnoreCase);
                    if (skeletonCache.ContainsKey(skeStem))
                        continue;
                    try
                    {
                        skeletonCache[skeStem] = SkeletonFile.Parse(skeFile);
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
            var matchedExt = Ps2SceneFile.SupportedExtensions
                .FirstOrDefault(ext => filename.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            var stem = matchedExt != null ? filename[..^matchedExt.Length] : filename;

            var outputPath = Path.Combine(output, stem + ".glb");

            try
            {
                var fileData = File.ReadAllBytes(file);
                var isThawSkin = ThawPs2SkinFile.IsThawPs2Skin(fileData);

                // Use per-file texture provider if we have a cache,
                // or try auto-detecting a companion TEX file for this specific scene
                var provider = textureProvider;
                if (provider == null && embedTextures)
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

                int tris;

                // PAK-extracted MDL: GEOM-style VIF → Ps2GeomGltfWriter
                if (Ps2GeomFile.IsPakMdl(fileData))
                {
                    var geomScene = Ps2GeomFile.ParsePakMdl(fileData);
                    tris = Ps2GeomGltfWriter.Write(geomScene, outputPath, provider);
                }
                else
                {
                    // Detect pre-compiled VIF/DMA .skin.ps2 (THAW or THUG2)
                    Ps2Scene scene;
                    if (isThawSkin)
                    {
                        // THUG2 pre-compiled: skip if .iskin.ps2 exists (higher quality)
                        var iskinFile = file.Replace(".skin.ps2", ".iskin.ps2",
                            StringComparison.OrdinalIgnoreCase);
                        if (File.Exists(iskinFile))
                        {
                            skipped++;
                            if (verbose)
                                AnsiConsole.MarkupLine(
                                    $"  {filename}: [yellow]pre-compiled VIF, skipped (.iskin.ps2 exists)[/]");
                            continue;
                        }

                        // Load companion .tex.ps2 raw bytes for DIRECT block setup/material mapping.
                        byte[]? companionTexData = null;
                        var texDir = Path.GetDirectoryName(file);
                        if (texDir != null)
                        {
                            var companionTex = CompanionSearch.FindCompanion(
                                texDir, stem, [".tex.ps2"], ["TEX", "Textures"]);
                            if (companionTex != null)
                                companionTexData = File.ReadAllBytes(companionTex);
                        }

                        scene = ThawPs2SkinFile.Parse(fileData, companionTexData);
                    }
                    else if (ThawPs2SkinFile.IsPakSkin(fileData))
                    {
                        scene = ThawPs2SkinFile.ParsePakSkin(fileData);
                    }
                    else
                    {
                        scene = Ps2SceneFile.Parse(fileData);
                    }

                    // Resolve skeleton: explicit > cache > auto-discover
                    var skeleton = explicitSkeleton;
                    if (skeleton == null && skeletonCache != null)
                        skeletonCache.TryGetValue(stem, out skeleton);
                    if (skeleton == null && filename.Contains(".skin.", StringComparison.OrdinalIgnoreCase))
                        skeleton = TryDiscoverSkeleton(file, stem, isThawSkin);

                    if (skeleton != null)
                    {
                        var useSkinnedExport = true;
                        if (isThawSkin)
                        {
                            var transferred = ThawPs2SkinningTransfer.TryApplyFromCompanion(scene, file, skeleton);
                            if (transferred is { SkinnedVertexCount: > 0 })
                            {
                                scene = transferred.Scene;
                            }
                            else
                            {
                                useSkinnedExport = false;
                                if (verbose)
                                    AnsiConsole.MarkupLine(
                                        $"  {filename}: [yellow]skeleton found but no THAW PC skin weights were discovered; exporting rigid[/]");
                            }
                        }

                        tris = useSkinnedExport
                            ? Ps2SceneGltfWriter.WriteSkinned(scene, skeleton, outputPath, provider)
                            : Ps2SceneGltfWriter.Write(scene, outputPath, provider);
                        if (useSkinnedExport && tris > 0) skinnedCount++;
                    }
                    else
                    {
                        tris = Ps2SceneGltfWriter.Write(scene, outputPath, provider);
                    }
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
                    AnsiConsole.MarkupLine(
                        $"  {filename}: [green]{tris:N0} triangles{texInfo}[/]");
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
    ///     Auto-discover a companion skeleton file for a .skin.ps2 file.
    ///     Searches: same directory → sibling SKE/ → ancestor walk (Skeletons/, SKE/).
    ///     Tries .ske.ps2 first (PS2-specific), then .ske (cross-platform, used by THPS4).
    /// </summary>
    private static Ps2Skeleton? TryDiscoverSkeleton(string skinFile, string stem, bool isThawSkin)
    {
        var skeFile = ThawSkeletonDiscovery.FindSkeletonPath(
            skinFile,
            stem,
            isThawSkin);
        if (skeFile == null) return null;

        try
        {
            return ParseSkeletonFile(skeFile);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Parse a skeleton file, routing to the correct parser based on extension.
    /// </summary>
    private static Ps2Skeleton ParseSkeletonFile(string path)
    {
        if (path.EndsWith(".ske.ps2", StringComparison.OrdinalIgnoreCase))
            return Ps2SkeletonFile.Parse(path);

        // Cross-platform .ske format (THPS4/THUG/THUG2)
        return SkeletonFile.Parse(path);
    }

    private static bool IsPs2SceneFile(string path)
    {
        var name = Path.GetFileName(path);
        return Ps2SceneFile.SupportedExtensions
            .Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }
}
