using System.Numerics;
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

    /// <summary>
    ///     Options for <see cref="Convert"/>. When <see cref="TexPath"/> is null the
    ///     PAK file itself is used as the zone-texture root (sibling zone PAKs are
    ///     picked up automatically).
    /// </summary>
    public readonly record struct WorldzoneOptions(
        string? TexPath = null,
        bool Combined = true,
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
        Ps2SceneGltfWriter.TextureProvider? textureProvider = null;
        Ps2GeomGltfWriter.Tex0Resolver? tex0Resolver = null;
        if (texPath != null)
        {
            ZoneTextureProviderBuilder.TryBuild(
                texPath,
                out textureProvider,
                out tex0Resolver,
                options.Log);
        }

        Directory.CreateDirectory(outputDir);

        var combinedScene = options.Combined ? new SceneBuilder() : null;
        var outputs = new List<string>();
        var converted = 0;
        var failed = 0;
        var skipped = 0;
        var totalTriangles = 0;
        var totalPlacements = 0;

        foreach (var mdlEntry in mdlEntries)
        {
            ct.ThrowIfCancellationRequested();

            var result = ProcessWorldzoneMdl(
                pakBytes, mdlEntry, outputDir, combinedScene,
                textureProvider, tex0Resolver, options.Log);

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
            var combinedPath = SaveCombinedWorldzone(source.EntryName, outputDir, combinedScene);
            outputs.Add(combinedPath);
        }

        return new WorldzoneResult(
            mdlEntries.Count, converted, failed, skipped,
            totalPlacements, totalTriangles, outputs);
    }

    private enum MdlOutcome { Converted, Skipped, Failed }

    private readonly record struct MdlResult(MdlOutcome Outcome, int Triangles, int Placements, string? OutputPath);

    private static MdlResult ProcessWorldzoneMdl(
        byte[] pakBytes,
        ArchiveEntry mdlEntry,
        string outputDir,
        SceneBuilder? combinedScene,
        Ps2SceneGltfWriter.TextureProvider? textureProvider,
        Ps2GeomGltfWriter.Tex0Resolver? tex0Resolver,
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
                return new MdlResult(MdlOutcome.Skipped, 0, 0, null);
            }

            var geomScene = Ps2GeomFile.ParsePakMdl(mdlData);
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
                scene, geomScene, rootPlacement, textureProvider, tex0Resolver,
                leafFilter: leaf => !leaf.IsLocalSpace);

            var localTris = bonePlacements.Count > 0
                ? Ps2GeomGltfWriter.AppendToScene(
                    scene, geomScene, bonePlacements, textureProvider, tex0Resolver,
                    leafFilter: leaf => leaf.IsLocalSpace)
                : 0;

            var tris = worldTris + localTris;
            var instancesCount = rootPlacement.Count + bonePlacements.Count;

            if (tris == 0)
            {
                log?.Invoke($"  {mdlName}.mdl: empty (0 triangles)");
                return new MdlResult(MdlOutcome.Skipped, 0, 0, null);
            }

            string? outputPath = null;
            if (combinedScene == null)
            {
                outputPath = Path.Combine(outputDir, $"{mdlName}.glb");
                var model = scene.ToGltf2();
                GltfNormalSmoother.SmoothNormals(model);
                model.SaveGLB(outputPath);
            }

            log?.Invoke(
                $"  {mdlName}.mdl: {instancesCount} placements (world={worldTris} + bones={localTris}) = {tris:N0} tris");
            return new MdlResult(MdlOutcome.Converted, tris, instancesCount, outputPath);
        }
        catch (Exception ex)
        {
            log?.Invoke($"  {mdlName}.mdl: {ex.Message}");
            return new MdlResult(MdlOutcome.Failed, 0, 0, null);
        }
    }

    private static readonly string[] CombinedWorldzoneStrippedExtensions = [".pak.ps2", ".pak"];

    private static string SaveCombinedWorldzone(string input, string output, SceneBuilder combinedScene)
    {
        var name = Path.GetFileName(input);
        var strippedExt = CombinedWorldzoneStrippedExtensions
            .FirstOrDefault(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
        var stem = strippedExt != null ? name[..^strippedExt.Length] : name;

        var combinedPath = Path.Combine(output, $"{stem}_worldzone.glb");
        var model = combinedScene.ToGltf2();
        GltfNormalSmoother.SmoothNormals(model);
        model.SaveGLB(combinedPath);
        return combinedPath;
    }
}
