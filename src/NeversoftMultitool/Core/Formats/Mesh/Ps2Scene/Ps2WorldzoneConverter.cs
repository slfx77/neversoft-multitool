using System.Numerics;
using System.Text;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene;
using SharpGLTF.Scenes;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;

/// <summary>
///     Converts a THAW worldzone .pak.ps2 (and its sibling PAKs) into glTF. Scans
///     the PAK for object (.mdl) and level-geometry entries, resolves per-bone
///     placements from the paired 0x91E1028D placement entry, and emits either
///     one .glb per MDL or a single combined .glb for the whole worldzone.
/// </summary>
public static class Ps2WorldzoneConverter
{
    public const uint WorldzoneMdlTypeHash = 0x9BCC234D;       // QbKey(".mdl") — object MDL
    public const uint WorldzoneLevelMdlTypeHash = 0x7EA7357B;  // THAW shell/CAP geometry chunk
    public const uint WorldzonePlacementTypeHash = 0x91E1028D;

    public enum WorldzoneTimeOfDay
    {
        All,
        Day,
        Night
    }

    /// <summary>
    ///     Options for <see cref="Convert"/>. When <see cref="TexPath"/> is null the
    ///     PAK file itself is used as the zone-texture root (sibling zone PAKs are
    ///     picked up automatically).
    /// </summary>
    public readonly record struct WorldzoneOptions(
        string? TexPath = null,
        bool Combined = true,
        bool DebugTextures = false,
        bool DebugLeafColors = false,
        WorldzoneTimeOfDay TimeOfDay = WorldzoneTimeOfDay.All,
        float CoordinateScale = 1f,
        Action<string>? Log = null);

    public readonly record struct WorldzoneResult(
        int MdlEntries,
        int Converted,
        int Failed,
        int Skipped,
        int Placements,
        int Triangles,
        IReadOnlyList<string> OutputPaths);

    /// <summary>
    ///     Cheap check used by the GUI scanner to decide whether a .pak.ps2 file
    ///     should appear as a worldzone mesh entry. Requires PAK magic, at least
    ///     one object/level MDL entry, and a paired placement entry.
    /// </summary>
    public static bool IsWorldzonePak(string pakPath)
    {
        try
        {
            if (!PakArchive.IsPakArchive(pakPath))
                return false;
            var typed = PakArchive.GetTypedEntries(pakPath);
            var hasMdl = typed.Any(e =>
                e.TypeHash == WorldzoneMdlTypeHash || e.TypeHash == WorldzoneLevelMdlTypeHash);
            if (!hasMdl) return false;
            return typed.Any(e => e.TypeHash == WorldzonePlacementTypeHash);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     Convert a worldzone PAK. Returns counts + the list of output file paths.
    ///     When no object/level MDL entries are present, returns a zero result and no output.
    /// </summary>
    public static WorldzoneResult Convert(
        string pakPath,
        string outputDir,
        WorldzoneOptions options = default,
        CancellationToken ct = default)
    {
        if (!File.Exists(pakPath))
            throw new FileNotFoundException("Worldzone PAK not found", pakPath);
        return Convert(new FileSystemAssetSource(pakPath), outputDir, options, ct);
    }

    /// <summary>
    ///     <see cref="AssetSource"/>-based entry point used by the GUI's mesh
    ///     converter so every archive type goes through the same abstraction. For
    ///     filesystem sources sibling-PAK zone-texture discovery is unchanged; for
    ///     archive-nested sources (not a supported scenario), sibling discovery is
    ///     skipped and only the main PAK's embedded textures apply.
    /// </summary>
    public static WorldzoneResult Convert(
        AssetSource source,
        string outputDir,
        WorldzoneOptions options = default,
        CancellationToken ct = default)
    {
        if (!float.IsFinite(options.CoordinateScale) || options.CoordinateScale <= 0f)
            throw new ArgumentOutOfRangeException(nameof(options), options.CoordinateScale,
                "Worldzone coordinate scale must be a finite positive value.");

        var pakBytes = source.ReadBytes();
        var typedEntries = PakArchive.GetTypedEntries(pakBytes);
        var mdlEntries = typedEntries
            .Where(e => e.TypeHash == WorldzoneMdlTypeHash || e.TypeHash == WorldzoneLevelMdlTypeHash)
            .Select(e => e.Entry)
            .ToList();

        if (mdlEntries.Count == 0)
            return new WorldzoneResult(0, 0, 0, 0, 0, 0, Array.Empty<string>());

        // Zone-texture discovery needs a real path so sibling-PAKs can be walked.
        // Explicit override via options.TexPath wins; otherwise fall back to the
        // source's backing path when available.
        var texPath = options.TexPath ?? source.FileSystemPath;
        var textureSourceHint = source.FileSystemPath ?? texPath;
        Ps2SceneGltfWriter.TextureProvider? textureProvider = null;
        Ps2GeomGltfWriter.Tex0Resolver? tex0Resolver = null;
        ZoneTextureCatalog? textureCatalog = null;
        if (texPath != null)
        {
            if (ZoneTextureCatalog.TryBuild(texPath, out textureCatalog, options.Log)
                && textureCatalog != null)
            {
                textureProvider = textureCatalog.CreateTextureProvider();
                tex0Resolver = textureCatalog.CreateTex0Resolver(textureSourceHint);
            }
        }

        Directory.CreateDirectory(outputDir);

        var combinedScene = options.Combined ? new SceneBuilder() : null;
        var debugLeafScene = options.DebugLeafColors ? new SceneBuilder() : null;
        var debugLeafRecords = new List<Ps2GeomLeafIdDebugRecord>();
        var nextDebugLeafId = 1;
        var outputs = new List<string>();
        var converted = 0;
        var failed = 0;
        var skipped = 0;
        var totalTriangles = 0;
        var totalPlacements = 0;
        var debugCollectors = new List<Ps2GeomDebugCollector>();
        var collectDebug = options.DebugTextures || options.DebugLeafColors;

        foreach (var mdlEntry in mdlEntries)
        {
            ct.ThrowIfCancellationRequested();

            var result = ProcessWorldzoneMdl(
                pakBytes, mdlEntry, outputDir, combinedScene,
                debugLeafScene, debugLeafRecords, ref nextDebugLeafId,
                textureProvider, tex0Resolver, textureCatalog,
                textureSourceHint, collectDebug, options.TimeOfDay, options.CoordinateScale, options.Log);
            if (result.DebugCollector != null)
                debugCollectors.Add(result.DebugCollector);

            switch (result.Outcome)
            {
                case MdlOutcome.Converted:
                    converted++;
                    totalTriangles += result.Triangles;
                    totalPlacements += result.Placements;
                    if (result.OutputPath != null)
                        outputs.Add(result.OutputPath);
                    break;
                case MdlOutcome.Skipped:
                    skipped++;
                    break;
                case MdlOutcome.Failed:
                    failed++;
                    break;
            }
        }

        if (options.Combined && combinedScene != null && totalTriangles > 0)
        {
            var combinedPath = SaveCombinedWorldzone(source.EntryName, outputDir, combinedScene, options.TimeOfDay);
            outputs.Add(combinedPath);
        }

        if (debugLeafScene != null && debugLeafRecords.Count > 0)
        {
            var debugPath = SaveCombinedWorldzoneLeafIds(
                source.EntryName, outputDir, debugLeafScene, options.TimeOfDay);
            outputs.Add(debugPath);
        }

        if (options.DebugTextures || options.DebugLeafColors)
            WriteWorldzoneDebug(outputDir, textureCatalog, debugCollectors, debugLeafRecords, options.DebugTextures);

        return new WorldzoneResult(
            mdlEntries.Count, converted, failed, skipped,
            totalPlacements, totalTriangles, outputs);
    }

    private enum MdlOutcome { Converted, Skipped, Failed }

    private readonly record struct MdlResult(
        MdlOutcome Outcome,
        int Triangles,
        int Placements,
        string? OutputPath,
        Ps2GeomDebugCollector? DebugCollector);

    private static MdlResult ProcessWorldzoneMdl(
        byte[] pakBytes,
        ArchiveEntry mdlEntry,
        string outputDir,
        SceneBuilder? combinedScene,
        SceneBuilder? debugLeafScene,
        List<Ps2GeomLeafIdDebugRecord> debugLeafRecords,
        ref int nextDebugLeafId,
        Ps2SceneGltfWriter.TextureProvider? textureProvider,
        Ps2GeomGltfWriter.Tex0Resolver? tex0Resolver,
        ZoneTextureCatalog? textureCatalog,
        string? textureSourceHint,
        bool collectDebug,
        WorldzoneTimeOfDay timeOfDay,
        float coordinateScale,
        Action<string>? log)
    {
        var mdlName = $"{mdlEntry.Offset:X8}";
        try
        {
            var mdlData = new byte[mdlEntry.Size];
            Array.Copy(pakBytes, mdlEntry.Offset, mdlData, 0, (int)mdlEntry.Size);

            if (!Ps2GeomFile.IsPakMdl(mdlData))
            {
                log?.Invoke($"  {mdlName}.mdl: not a PAK MDL, skipped");
                return new MdlResult(MdlOutcome.Skipped, 0, 0, null, null);
            }

            var mdlTextureHint = textureCatalog?.FindTextureEntryHintBefore(textureSourceHint, mdlEntry.Offset)
                ?? textureSourceHint;
            var mdlTex0Resolver = textureCatalog?.CreateTex0Resolver(mdlTextureHint)
                ?? tex0Resolver;
            var debugCollector = collectDebug
                ? new Ps2GeomDebugCollector(mdlName)
                {
                    TextureResolver = textureCatalog?.CreateDebugTex0Resolver(mdlTextureHint)
                }
                : null;

            Action<Ps2GeomLeafRejection>? rejectionLogger = debugCollector != null
                ? rejection => debugCollector.AddRejection(rejection)
                : null;
            var geomScene = Ps2GeomFile.ParsePakMdl(
                mdlData,
                mdlName,
                rejectionLogger);
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
                // Level MDLs lack a root bone. Their raw vertex data is already in a
                // Y-up (glTF-friendly) convention, so identity is the right fallback.
                rootPlacement.Add((Vector3.Zero, Quaternion.Identity));
            }

            var scene = combinedScene ?? new SceneBuilder();

            // IsLodPlane was originally added (alongside the wrong-attribute decode bug) to
            // suppress thin flat artifacts that pierced nearby geometry. With vertex
            // positions now decoded correctly, the same heuristic was rejecting 1,698 / 4,149
            // legitimate worldzone leaves on z_bh — flat sidewalks, walls, rooftops, fence
            // panels — leaving the user's screenshots full of holes. Keep the IsLocalSpace
            // exclusion (cars/objects that need bone-instancing) but drop the IsLodPlane
            // exclusion entirely.
            var worldTris = Ps2GeomGltfWriter.AppendToScene(
                scene, geomScene, rootPlacement, textureProvider, mdlTex0Resolver,
                leafFilter: leaf => !leaf.IsLocalSpace && ShouldIncludeForTimeOfDay(leaf, timeOfDay),
                debugCollector: debugCollector,
                localizeMeshOrigins: true,
                coordinateScale: coordinateScale);

            var localTris = bonePlacements.Count > 0
                ? Ps2GeomGltfWriter.AppendToScene(
                    scene, geomScene, bonePlacements, textureProvider, mdlTex0Resolver,
                    leafFilter: leaf => leaf.IsLocalSpace && ShouldIncludeForTimeOfDay(leaf, timeOfDay),
                    debugCollector: debugCollector,
                    localizeMeshOrigins: true,
                    coordinateScale: coordinateScale)
                : 0;

            if (debugLeafScene != null)
            {
                Ps2GeomGltfWriter.AppendLeafIdDebugScene(
                    debugLeafScene,
                    geomScene,
                    rootPlacement,
                    leaf => !leaf.IsLocalSpace && ShouldIncludeForTimeOfDay(leaf, timeOfDay),
                    mdlName,
                    debugLeafRecords,
                    ref nextDebugLeafId,
                    mdlTex0Resolver,
                    debugCollector?.TextureResolver,
                    coordinateScale);
                if (bonePlacements.Count > 0)
                {
                    Ps2GeomGltfWriter.AppendLeafIdDebugScene(
                        debugLeafScene,
                        geomScene,
                        bonePlacements,
                        leaf => leaf.IsLocalSpace && ShouldIncludeForTimeOfDay(leaf, timeOfDay),
                        mdlName,
                        debugLeafRecords,
                        ref nextDebugLeafId,
                        mdlTex0Resolver,
                        debugCollector?.TextureResolver,
                        coordinateScale);
                }
            }

            var tris = worldTris + localTris;
            var instancesCount = rootPlacement.Count + bonePlacements.Count;

            if (tris == 0)
            {
                log?.Invoke($"  {mdlName}.mdl: empty (0 triangles)");
                return new MdlResult(MdlOutcome.Skipped, 0, 0, null, debugCollector);
            }

            string? outputPath = null;
            if (combinedScene == null)
            {
                outputPath = Path.Combine(outputDir, $"{mdlName}{GetVariantSuffix(timeOfDay)}.glb");
                var model = scene.ToGltf2();
                GltfNormalSmoother.SmoothNormals(model);
                model.SaveGLB(outputPath);
                Ps2GeomGltfWriter.EnsureExplicitTextureSamplers(outputPath);
            }

            log?.Invoke(
                $"  {mdlName}.mdl: {instancesCount} placements (world={worldTris} + bones={localTris}) = {tris:N0} tris");
            return new MdlResult(MdlOutcome.Converted, tris, instancesCount, outputPath, debugCollector);
        }
        catch (Exception ex)
        {
            log?.Invoke($"  {mdlName}.mdl: {ex.Message}");
            return new MdlResult(MdlOutcome.Failed, 0, 0, null, null);
        }
    }

    private static readonly string[] CombinedWorldzoneStrippedExtensions = [".pak.ps2", ".pak"];

    private static bool ShouldIncludeForTimeOfDay(Ps2GeomLeaf leaf, WorldzoneTimeOfDay timeOfDay)
    {
        if (timeOfDay == WorldzoneTimeOfDay.All || timeOfDay == WorldzoneTimeOfDay.Night)
            return true;

        return Ps2GeomGltfWriter.ClassifyWorldzoneRenderLayer(leaf) != Ps2GeomRenderLayer.NightOverlay;
    }

    private static void WriteWorldzoneDebug(
        string outputDir,
        ZoneTextureCatalog? textureCatalog,
        IReadOnlyList<Ps2GeomDebugCollector> collectors,
        IReadOnlyList<Ps2GeomLeafIdDebugRecord> leafIdRecords,
        bool dumpTextures)
    {
        var debugDir = Path.Combine(outputDir, "worldzone_debug");
        Directory.CreateDirectory(debugDir);
        if (dumpTextures)
            textureCatalog?.WriteDebugDump(debugDir);
        WriteMaterialsCsv(Path.Combine(debugDir, "materials.csv"), collectors);
        WriteLeafRejectionsCsv(Path.Combine(debugDir, "leaf_rejections.csv"), collectors);
        WriteLeafIdColorsCsv(Path.Combine(debugDir, "leaf_id_colors.csv"), leafIdRecords);
    }

    private static void WriteMaterialsCsv(
        string path,
        IReadOnlyList<Ps2GeomDebugCollector> collectors)
    {
        var sb = new StringBuilder();
        sb.AppendLine("mdl,material,texture_checksum,group_checksum,tex0,tex1,clamp1,alpha1,test1,alpha_mode,resolve_mode,source,entry,render_layer,triangles,min_x,min_y,min_z,max_x,max_y,max_z,is_billboard");
        foreach (var record in collectors.SelectMany(static collector => collector.Materials))
        {
            sb.Append(Csv(record.MdlName)).Append(',')
                .Append(Csv(record.MaterialName)).Append(',')
                .Append(CsvHex(record.TextureChecksum)).Append(',')
                .Append(CsvHex(record.GroupChecksum)).Append(',')
                .Append(CsvHex(record.Tex0)).Append(',')
                .Append(CsvHex(record.Tex1)).Append(',')
                .Append(CsvHex(record.Clamp1)).Append(',')
                .Append(CsvHex(record.Alpha1)).Append(',')
                .Append(CsvHex(record.Test1)).Append(',')
                .Append(Csv(record.AlphaMode)).Append(',')
                .Append(Csv(record.ResolveMode)).Append(',')
                .Append(Csv(record.SourceLabel)).Append(',')
                .Append(Csv(record.EntryLabel)).Append(',')
                .Append(Csv(record.RenderLayer.ToString())).Append(',')
                .Append(record.Triangles).Append(',')
                .Append(Float(record.Min.X)).Append(',')
                .Append(Float(record.Min.Y)).Append(',')
                .Append(Float(record.Min.Z)).Append(',')
                .Append(Float(record.Max.X)).Append(',')
                .Append(Float(record.Max.Y)).Append(',')
                .Append(Float(record.Max.Z)).Append(',')
                .Append(record.IsBillboard ? "true" : "false")
                .AppendLine();
        }

        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteLeafIdColorsCsv(
        string path,
        IReadOnlyList<Ps2GeomLeafIdDebugRecord> records)
    {
        var sb = new StringBuilder();
        sb.AppendLine("id,color,mdl,leaf_index,material,texture_checksum,group_checksum,tex0,tex1,clamp1,alpha1,test1,resolve_mode,source,entry,render_layer,triangles,placement_count,min_x,min_y,min_z,max_x,max_y,max_z,is_billboard,is_local_space");
        foreach (var record in records)
        {
            sb.Append(record.Id).Append(',')
                .Append(Csv(record.ColorHex)).Append(',')
                .Append(Csv(record.MdlName)).Append(',')
                .Append(record.LeafIndex).Append(',')
                .Append(Csv(record.MaterialName)).Append(',')
                .Append(CsvHex(record.TextureChecksum)).Append(',')
                .Append(CsvHex(record.GroupChecksum)).Append(',')
                .Append(CsvHex(record.Tex0)).Append(',')
                .Append(CsvHex(record.Tex1)).Append(',')
                .Append(CsvHex(record.Clamp1)).Append(',')
                .Append(CsvHex(record.Alpha1)).Append(',')
                .Append(CsvHex(record.Test1)).Append(',')
                .Append(Csv(record.ResolveMode)).Append(',')
                .Append(Csv(record.SourceLabel)).Append(',')
                .Append(Csv(record.EntryLabel)).Append(',')
                .Append(Csv(record.RenderLayer.ToString())).Append(',')
                .Append(record.Triangles).Append(',')
                .Append(record.PlacementCount).Append(',')
                .Append(Float(record.Min.X)).Append(',')
                .Append(Float(record.Min.Y)).Append(',')
                .Append(Float(record.Min.Z)).Append(',')
                .Append(Float(record.Max.X)).Append(',')
                .Append(Float(record.Max.Y)).Append(',')
                .Append(Float(record.Max.Z)).Append(',')
                .Append(record.IsBillboard ? "true" : "false").Append(',')
                .Append(record.IsLocalSpace ? "true" : "false")
                .AppendLine();
        }

        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteLeafRejectionsCsv(
        string path,
        IReadOnlyList<Ps2GeomDebugCollector> collectors)
    {
        var sb = new StringBuilder();
        sb.AppendLine("mdl,stage,reason,leaf_index,vertex_count,tex0,min_x,min_y,min_z,max_x,max_y,max_z");
        foreach (var record in collectors.SelectMany(static collector => collector.LeafRejections))
        {
            sb.Append(Csv(record.MdlName)).Append(',')
                .Append(Csv(record.Stage)).Append(',')
                .Append(Csv(record.Reason)).Append(',')
                .Append(record.LeafIndex).Append(',')
                .Append(record.VertexCount).Append(',')
                .Append(CsvHex(record.Tex0)).Append(',')
                .Append(Float(record.Min.X)).Append(',')
                .Append(Float(record.Min.Y)).Append(',')
                .Append(Float(record.Min.Z)).Append(',')
                .Append(Float(record.Max.X)).Append(',')
                .Append(Float(record.Max.Y)).Append(',')
                .Append(Float(record.Max.Z))
                .AppendLine();
        }

        File.WriteAllText(path, sb.ToString());
    }

    private static string SaveCombinedWorldzone(
        string input,
        string output,
        SceneBuilder combinedScene,
        WorldzoneTimeOfDay timeOfDay)
    {
        var name = Path.GetFileName(input);
        var strippedExt = CombinedWorldzoneStrippedExtensions
            .FirstOrDefault(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
        var stem = strippedExt != null ? name[..^strippedExt.Length] : name;

        var combinedPath = Path.Combine(output, $"{stem}_worldzone{GetVariantSuffix(timeOfDay)}.glb");
        var model = combinedScene.ToGltf2();
        GltfNormalSmoother.SmoothNormals(model);
        model.SaveGLB(combinedPath);
        Ps2GeomGltfWriter.EnsureExplicitTextureSamplers(combinedPath);
        return combinedPath;
    }

    private static string SaveCombinedWorldzoneLeafIds(
        string input,
        string output,
        SceneBuilder debugLeafScene,
        WorldzoneTimeOfDay timeOfDay)
    {
        var name = Path.GetFileName(input);
        var strippedExt = CombinedWorldzoneStrippedExtensions
            .FirstOrDefault(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
        var stem = strippedExt != null ? name[..^strippedExt.Length] : name;

        var combinedPath = Path.Combine(output, $"{stem}_worldzone{GetVariantSuffix(timeOfDay)}_leaf_ids.glb");
        var model = debugLeafScene.ToGltf2();
        model.SaveGLB(combinedPath);
        return combinedPath;
    }

    private static string GetVariantSuffix(WorldzoneTimeOfDay timeOfDay) =>
        timeOfDay switch
        {
            WorldzoneTimeOfDay.Day => "_day",
            WorldzoneTimeOfDay.Night => "_night",
            _ => ""
        };

    private static string Csv(string value)
    {
        var escaped = value.Replace("\"", "\"\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    private static string CsvHex(uint value) => $"0x{value:X8}";

    private static string CsvHex(ulong value) => $"0x{value:X16}";

    private static string Float(float value) =>
        value.ToString("G9", System.Globalization.CultureInfo.InvariantCulture);
}
