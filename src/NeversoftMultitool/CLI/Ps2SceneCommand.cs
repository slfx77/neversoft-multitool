using System.CommandLine;
using System.Diagnostics;
using System.Numerics;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skin;
using NeversoftMultitool.Core.Formats.Texture;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene;
using SharpGLTF.Scenes;
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
            Description =
                "Skeleton file (.ske.ps2 or .ske) or directory. Auto-discovered for .skin.ps2 files if not specified."
        };
        var worldzoneOption = new Option<bool>("--worldzone")
        {
            Description =
                "Treat the input .pak.ps2 as a THAW worldzone and place every .mdl entry at the positions recovered from its paired .91E1028D placement entry."
        };
        var worldzoneCombinedOption = new Option<bool>("--worldzone-combined")
        {
            Description =
                "When --worldzone is set, emit a single combined .glb containing every placed object across all MDLs in the PAK."
        };
        var command = new Command("ps2scene", "Convert PS2 scene files (MDL/SKIN) to glTF (.glb)");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(texPathOption);
        command.Options.Add(verboseOption);
        command.Options.Add(skeletonOption);
        command.Options.Add(worldzoneOption);
        command.Options.Add(worldzoneCombinedOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var texPath = parseResult.GetValue(texPathOption);
            var verbose = parseResult.GetValue(verboseOption);
            var skePath = parseResult.GetValue(skeletonOption);
            var worldzone = parseResult.GetValue(worldzoneOption);
            var worldzoneCombined = parseResult.GetValue(worldzoneCombinedOption);

            if (worldzone)
                return Task.FromResult(ExecuteWorldzone(input, output, texPath, worldzoneCombined, verbose));
            return Task.FromResult(Execute(input, output, texPath, verbose, skePath));
        });

        return command;
    }

    private const uint WorldzoneMdlTypeHash = 0x9BCC234D;   // QbKey(".mdl") — standard object MDL
    private const uint WorldzoneLevelMdlTypeHash = 0x7EA7357B; // THAW shell/CAP level-geometry chunk
    private const uint WorldzonePlacementTypeHash = 0x91E1028D;

    private sealed class WorldzoneRunStats
    {
        public int Converted;
        public int Failed;
        public int Skipped;
        public int TotalTriangles;
        public int TotalPlacements;
    }

    private sealed record WorldzoneContext(
        byte[] PakBytes,
        string OutputDir,
        bool Combined,
        SceneBuilder? CombinedScene,
        Ps2SceneGltfWriter.TextureProvider? TextureProvider,
        Ps2GeomGltfWriter.Tex0Resolver? Tex0Resolver,
        bool Verbose);

    private static int ExecuteWorldzone(string input, string output,
        string? texPath, bool combined, bool verbose)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] --worldzone requires an existing .pak.ps2 file: {input}");
            return 1;
        }

        var typedEntries = PakArchive.GetTypedEntries(input);
        if (typedEntries.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] no PAK entries found in {input}");
            return 1;
        }

        var mdlEntries = FindWorldzoneMdls(typedEntries);
        if (mdlEntries.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No .mdl entries found in {input}.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine(
            $"Worldzone [green]{Path.GetFileName(input)}[/]: " +
            $"{mdlEntries.Count} .mdl entrie(s){(combined ? ", emitting combined .glb" : "")}");

        // Build texture providers (same strategy as non-worldzone path: THAW zone TEX first, fall back to standard)
        Ps2TextureLoader.TryBuildZoneTexProviders(
            texPath,
            out var textureProvider,
            out var tex0Resolver,
            verbose);

        Directory.CreateDirectory(output);
        var context = new WorldzoneContext(
            PakBytes: File.ReadAllBytes(input),
            OutputDir: output,
            Combined: combined,
            CombinedScene: combined ? new SceneBuilder() : null,
            TextureProvider: textureProvider,
            Tex0Resolver: tex0Resolver,
            Verbose: verbose);

        var stopwatch = Stopwatch.StartNew();
        var stats = new WorldzoneRunStats();

        foreach (var mdlEntry in mdlEntries)
            ProcessWorldzoneMdl(context, mdlEntry, stats);

        if (combined && context.CombinedScene != null && stats.TotalTriangles > 0)
            SaveCombinedWorldzone(input, output, context.CombinedScene);

        stopwatch.Stop();
        var skipMsg = stats.Skipped > 0 ? $", {stats.Skipped} skipped" : "";
        AnsiConsole.MarkupLine(
            $"Worldzone: [green]{stats.Converted}[/]/{mdlEntries.Count} MDL(s), " +
            $"[green]{stats.TotalPlacements}[/] placements, " +
            $"{stats.TotalTriangles:N0} triangles, {stats.Failed} failed{skipMsg} " +
            $"in {stopwatch.Elapsed.TotalSeconds:F2}s");
        return 0;
    }

    private static List<ArchiveEntry> FindWorldzoneMdls(
        List<(uint TypeHash, ArchiveEntry Entry)> typedEntries)
    {
        // Include both QbKey(".mdl") = 0x9BCC234D (standard object MDLs) and the THAW
        // shell/CAP geometry-chunk variant 0x7EA7357B, which is how z_bh's 3.2 MB level
        // geometry entry is tagged in the PAK table.
        return typedEntries
            .Where(e => e.TypeHash == WorldzoneMdlTypeHash
                        || e.TypeHash == WorldzoneLevelMdlTypeHash)
            .Select(e => e.Entry)
            .ToList();
    }

    private static void ProcessWorldzoneMdl(
        WorldzoneContext ctx, ArchiveEntry mdlEntry, WorldzoneRunStats stats)
    {
        var mdlName = $"{mdlEntry.Offset:X8}";
        try
        {
            var mdlData = new byte[mdlEntry.Size];
            Array.Copy(ctx.PakBytes, mdlEntry.Offset, mdlData, 0, (int)mdlEntry.Size);

            if (!Ps2GeomFile.IsPakMdl(mdlData))
            {
                stats.Skipped++;
                if (ctx.Verbose)
                    AnsiConsole.MarkupLine($"  {mdlName}.mdl: [yellow]not a PAK MDL, skipped[/]");
                return;
            }

            var geomScene = Ps2GeomFile.ParsePakMdl(mdlData);

            // Two cases:
            //   Object MDL: has bones → split world/local leaves, instance per non-root bone.
            //   Level MDL: no bones (e.g. 003B1940 — 3.2 MB street/building geometry) →
            //              emit all leaves at a PS2→glTF axis-swap identity node.
            var hasBones = geomScene.MdlPreamble?.Bones.Count > 0;
            var placements = hasBones
                ? Ps2MdlPlacementResolver.ResolveWorldzonePlacements(geomScene.MdlPreamble!)
                : [];

            var rootPlacement = new List<(Vector3, Quaternion)>(1);
            var bonePlacements = new List<(Vector3, Quaternion)>();
            if (placements.Count > 0)
            {
                rootPlacement.Add((placements[0].Position, placements[0].Rotation));
                bonePlacements.AddRange(placements.Skip(1).Select(p => (p.Position, p.Rotation)));
            }
            else
            {
                // Level MDLs lack a root bone. Their raw vertex data appears to already be in a
                // Y-up (glTF-friendly) convention — object MDLs' bone-driven swap rotates them
                // 90° toward the camera, so identity is the right fallback here.
                rootPlacement.Add((Vector3.Zero, Quaternion.Identity));
            }

            var scene = ctx.Combined ? ctx.CombinedScene! : new SceneBuilder();

            // Pass 1: world-space leaves (shared infrastructure) at root axis-swap only.
            // LOD-plane billboards are dropped here (Problem B) — they're thin polygons that
            // the engine would swap in at distance, but they just cut through our final mesh.
            var worldTris = Ps2GeomGltfWriter.AppendToScene(
                scene, geomScene, rootPlacement, ctx.TextureProvider, ctx.Tex0Resolver,
                leafFilter: leaf => !leaf.IsLocalSpace && !leaf.IsLodPlane);

            // Pass 2: local-space leaves (car sectors) duplicated per non-root bone.
            var localTris = bonePlacements.Count > 0
                ? Ps2GeomGltfWriter.AppendToScene(
                    scene, geomScene, bonePlacements, ctx.TextureProvider, ctx.Tex0Resolver,
                    leafFilter: leaf => leaf.IsLocalSpace)
                : 0;

            var tris = worldTris + localTris;
            var instancesCount = rootPlacement.Count + bonePlacements.Count;

            if (tris == 0)
            {
                stats.Skipped++;
                if (ctx.Verbose)
                    AnsiConsole.MarkupLine($"  {mdlName}.mdl: [yellow]empty (0 triangles)[/]");
                return;
            }

            if (!ctx.Combined)
            {
                var model = scene.ToGltf2();
                GltfNormalSmoother.SmoothNormals(model);
                model.SaveGLB(Path.Combine(ctx.OutputDir, $"{mdlName}.glb"));
            }

            stats.Converted++;
            stats.TotalTriangles += tris;
            stats.TotalPlacements += instancesCount;
            if (ctx.Verbose)
                AnsiConsole.MarkupLine(
                    $"  {mdlName}.mdl: [green]{instancesCount} placements "
                    + $"(world={worldTris} + bones={localTris}) = {tris:N0} tris[/]");
        }
        catch (Exception ex)
        {
            stats.Failed++;
            if (ctx.Verbose)
                AnsiConsole.MarkupLine($"  {mdlName}.mdl: [red]{ex.Message.EscapeMarkup()}[/]");
        }
    }

    private static void SaveCombinedWorldzone(string input, string output, SceneBuilder combinedScene)
    {
        var name = Path.GetFileName(input);
        var strippedExt = new[] { ".pak.ps2", ".pak" }
            .FirstOrDefault(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
        var stem = strippedExt != null ? name[..^strippedExt.Length] : name;

        var combinedPath = Path.Combine(output, $"{stem}_worldzone.glb");
        var model = combinedScene.ToGltf2();
        GltfNormalSmoother.SmoothNormals(model);
        model.SaveGLB(combinedPath);
        AnsiConsole.MarkupLine($"Wrote combined worldzone: [green]{combinedPath}[/]");
    }

    private static int Execute(string input, string output,
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
        Ps2GeomGltfWriter.Tex0Resolver? tex0Resolver = null;
        Dictionary<uint, Ps2Texture>? textureCache = null;

        // Try THAW world-zone TEX format first (GIF A+D VRAM uploads)
        if (!Ps2TextureLoader.TryBuildZoneTexProviders(
                texPath, out textureProvider, out tex0Resolver, verbose))
        {
            // Fall back to standard TEX formats
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

            var tex0Mapping = Ps2TextureLoader.BuildTex0Mapping(files, texPath, verbose);
            if (tex0Mapping.Count > 0)
            {
                if (verbose)
                {
                    AnsiConsole.MarkupLine(
                        $"Built TEX0 mapping with [green]{tex0Mapping.Count}[/] entries");
                }

                tex0Resolver = (dmaTex0, groupChecksum) =>
                {
                    var key = Ps2VramAllocator.DecodeTex0Key(dmaTex0, groupChecksum);
                    return tex0Mapping.GetValueOrDefault(key);
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

                int tris;

                // PAK-extracted MDL: GEOM-style VIF → Ps2GeomGltfWriter
                if (Ps2GeomFile.IsPakMdl(fileData))
                {
                    var geomScene = Ps2GeomFile.ParsePakMdl(fileData);
                    tris = Ps2GeomGltfWriter.Write(geomScene, outputPath, provider, tex0Resolver);
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
