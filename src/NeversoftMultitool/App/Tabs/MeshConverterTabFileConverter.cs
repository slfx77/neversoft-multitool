using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Collision;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ddm;
using NeversoftMultitool.Core.Formats.Mesh.Detection;
using NeversoftMultitool.Core.Formats.Mesh.Lit;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skin;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;
using NeversoftMultitool.Core.Formats.Texture;
using NeversoftMultitool.Core.Formats.Texture.Ps2;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.SceneTex;
using NeversoftMultitool.Core.Formats.Texture.Psx;
using NeversoftMultitool.Core.Formats.Texture.RenderWare;
using SharpGLTF.Schema2;

namespace NeversoftMultitool;

internal static class MeshConverterTabFileConverter
{
    private static readonly MeshModelParser Parser = new();

    internal static readonly string[] Ps2TexExtensions = [".tex.ps2", ".tex", ".img.ps2"];
    internal static readonly string[] Ps2TexSubdirs = ["TEX", "Textures", "IMG"];
    internal static readonly string[] PcSkinExtensions = [".skin.wpc", ".skin.xbx"];
    internal static readonly string[] PcSkinSubdirs = ["SKIN", "Models"];

    /// <summary>
    ///     Converts a mesh file to GLB bytes in memory (no temp files).
    ///     Used by the preview panel for on-select 3D viewing.
    /// </summary>
    public static (byte[]? GlbBytes, int Triangles) ConvertToGlbBytes(
        MeshFileEntry entry,
        WorldzoneTimeOfDay worldzoneTimeOfDay = WorldzoneTimeOfDay.All,
        float worldzoneScale = 1f,
        IReadOnlyDictionary<string, bool>? visibilityOverrides = null,
        bool includeLevelObjects = true,
        Ps2Skeleton? preparedSkeleton = null)
    {
        var (glbBytes, triangles, _) = ConvertToGlbPreview(
            entry,
            worldzoneTimeOfDay,
            worldzoneScale,
            visibilityOverrides,
            includeLevelObjects,
            preparedSkeleton);
        return (glbBytes, triangles);
    }

    public static (byte[]? GlbBytes, int Triangles, IReadOnlyList<ModelVisibilityGroup> VisibilityGroups)
        ConvertToGlbPreview(
            MeshFileEntry entry,
            WorldzoneTimeOfDay worldzoneTimeOfDay = WorldzoneTimeOfDay.All,
            float worldzoneScale = 1f,
            IReadOnlyDictionary<string, bool>? visibilityOverrides = null,
            bool includeLevelObjects = true,
            Ps2Skeleton? preparedSkeleton = null)
    {
        var effectiveWorldzoneScale = MeshGuiCoordinateScalePolicy.Resolve(
            entry.IsPakWorldzone, worldzoneScale);
        var document = Parser.Parse(CreateImportRequest(
            entry,
            worldzoneTimeOfDay,
            effectiveWorldzoneScale,
            visibilityOverrides,
            includeLevelObjects,
            preparedSkeleton));
        var groups = document.VisibilityGroups.ToArray();
        var (glbBytes, triangles) = ModelExportService.BuildGlbBytes(document);
        return (glbBytes, triangles, groups);
    }

    public static MeshExportResult ConvertFile(
        MeshFileEntry entry,
        string outputDir,
        WorldzoneTimeOfDay worldzoneTimeOfDay = WorldzoneTimeOfDay.All,
        float worldzoneScale = 1f,
        MeshOutputFormat outputFormat = MeshOutputFormat.Glb,
        string? outputStem = null,
        IReadOnlyDictionary<string, bool>? visibilityOverrides = null,
        string? blenderHelperPath = null,
        bool includeLevelObjects = true,
        Ps2Skeleton? preparedSkeleton = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveWorldzoneScale = MeshGuiCoordinateScalePolicy.Resolve(
            entry.IsPakWorldzone, worldzoneScale);
        var document = Parser.Parse(CreateImportRequest(
            entry,
            worldzoneTimeOfDay,
            effectiveWorldzoneScale,
            visibilityOverrides,
            includeLevelObjects,
            preparedSkeleton));
        return ModelExportService.Export(
            document,
            new MeshExportRequest
            {
                OutputDirectory = outputDir,
                OutputStem = string.IsNullOrWhiteSpace(outputStem) ? document.Name : outputStem,
                Format = outputFormat,
                BlenderHelperPath = blenderHelperPath,
                WorldzoneTimeOfDay = worldzoneTimeOfDay,
                WorldzoneScale = effectiveWorldzoneScale,
                CancellationToken = cancellationToken
            });
    }

    private static MeshImportRequest CreateImportRequest(
        MeshFileEntry entry,
        WorldzoneTimeOfDay worldzoneTimeOfDay = WorldzoneTimeOfDay.All,
        float worldzoneScale = 1f,
        IReadOnlyDictionary<string, bool>? visibilityOverrides = null,
        bool includeLevelObjects = true,
        Ps2Skeleton? preparedSkeleton = null)
    {
        return new MeshImportRequest
        {
            Source = entry.Source,
            FileName = entry.FileName,
            OutputStem = GetOutputStem(entry),
            SourceKind = GetSourceKind(entry),
            Ps2SubFormat = entry.Ps2SubFormat,
            HasPlacedPsxCompanion = entry.HasPlacedPsxCompanion,
            VisibilityOverrides = visibilityOverrides,
            IncludeLevelObjects = includeLevelObjects,
            WorldzoneTimeOfDay = worldzoneTimeOfDay,
            WorldzoneScale = MeshGuiCoordinateScalePolicy.Resolve(
                entry.IsPakWorldzone, worldzoneScale),
            PreparedSkeleton = entry.SupportsExplicitXbxSkeleton
                ? preparedSkeleton
                : null
        };
    }

    private static ModelSourceKind GetSourceKind(MeshFileEntry entry)
    {
        if (entry.IsCol) return ModelSourceKind.Collision;
        if (entry.IsPakWorldzone) return ModelSourceKind.Ps2Worldzone;
        if (entry.IsPs2Scene) return ModelSourceKind.Ps2Scene;
        if (entry.IsPs2Geom) return ModelSourceKind.Ps2Geom;
        if (entry.IsXbxScene) return ModelSourceKind.XbxScene;
        if (entry.IsRwBsp) return ModelSourceKind.RenderWareBsp;
        if (entry.IsRwDff) return ModelSourceKind.RenderWareDff;
        if (entry.IsN64Model) return ModelSourceKind.N64Model;
        if (entry.IsPsx) return ModelSourceKind.Psx;
        return ModelSourceKind.Ddm;
    }

    private static string GetOutputStem(MeshFileEntry entry)
    {
        if (entry.IsN64Model)
            return MeshTypeDetector.GetN64BundleStem(entry.FileName);

        return MeshConverterTabFileScanner.StripCompoundExtension(entry.FileName);
    }

    internal static Ps2Skeleton? TryLoadPs2Skeleton(MeshFileEntry entry, string stem)
    {
        // Prefer exact companion (.ske.ps2 then .ske). Scanner may have pre-resolved
        // a skeleton from a filesystem-wide index (ThawSkeletonDiscovery); reuse by
        // re-reading via Source so the parse works uniformly for both backings.
        var ps2Bytes = entry.Source.TryReadCompanion(stem + ".ske.ps2");
        if (ps2Bytes != null)
        {
            try
            {
                return Ps2SkeletonFile.Parse(ps2Bytes);
            }
            catch
            {
                /* fall through */
            }
        }

        // Cross-platform .ske and GC big-endian .ske.ngc both route through
        // SkeletonFile.Parse (which gates the THAW variant first).
        foreach (var extension in new[] { ".ske", ".ske.ngc" })
        {
            var skeBytes = entry.Source.TryReadCompanion(stem + extension);
            if (skeBytes == null)
                continue;
            try
            {
                return SkeletonFile.Parse(skeBytes);
            }
            catch
            {
                /* fall through */
            }
        }

        // THAW filesystem fallback: walk Builds/ tree for a humanoid-rig match when
        // the exact companion is missing. Only applies to filesystem-backed sources.
        if (entry.Ps2SubFormat == Ps2SceneSubFormat.ThawSkin && entry.Source.FileSystemPath != null)
        {
            var skeletonPath = ThawSkeletonDiscovery.FindSkeletonPath(
                entry.Source.FileSystemPath, stem, isThawSkin: true);
            if (skeletonPath != null)
            {
                try
                {
                    return skeletonPath.EndsWith(".ske.ps2", StringComparison.OrdinalIgnoreCase)
                        ? Ps2SkeletonFile.Parse(skeletonPath)
                        : SkeletonFile.Parse(skeletonPath);
                }
                catch
                {
                    /* proceed without skeleton */
                }
            }
        }

        // Archive fallback: ThawSkeletonDiscovery scoring over the archive's own
        // entry list. Catches humanoid rigs that share a PAK but don't have an
        // exact-stem skeleton (e.g. character models reusing human.ske.ps2).
        if (entry.Ps2SubFormat == Ps2SceneSubFormat.ThawSkin && entry.Source is ArchiveAssetSource archiveSource)
        {
            var archiveResult = ThawSkeletonDiscovery.FindInArchive(
                archiveSource.Backend.Entries, archiveSource.Backend, stem, isThawSkin: true);
            if (archiveResult is { } result)
            {
                try
                {
                    return result.EntryName.EndsWith(".ske.ps2", StringComparison.OrdinalIgnoreCase)
                        ? Ps2SkeletonFile.Parse(result.Bytes)
                        : SkeletonFile.Parse(result.Bytes);
                }
                catch
                {
                    /* proceed without skeleton */
                }
            }
        }

        return null;
    }

    internal static MeshNamedTextureResolver? BuildRwDffTextureProvider(MeshFileEntry entry)
        => BuildRwTxdTextureProvider(entry);

    // Delegates to the Core resolver so the THPS3 LOD-stem fallback
    // (*_LOD00.skn → base .tex) applies to the GUI preview path too.
    private static MeshNamedTextureResolver? BuildRwTxdTextureProvider(MeshFileEntry entry)
        => MeshCompanionResolver.BuildRwTxdTextureProvider(entry.Source, entry.FileName);

}
