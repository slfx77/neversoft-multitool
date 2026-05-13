using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Collision;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ddm;
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
    private static readonly string[] RwTexExtensions = [".tex"];
    private static readonly string[] RwTexSubdirs = ["TEX", "Textures"];
    internal static readonly string[] PcSkinExtensions = [".skin.wpc", ".skin.xbx"];
    internal static readonly string[] PcSkinSubdirs = ["SKIN", "Models"];

    /// <summary>
    ///     Converts a mesh file to GLB bytes in memory (no temp files).
    ///     Used by the preview panel for on-select 3D viewing.
    /// </summary>
    public static (byte[]? GlbBytes, int Triangles) ConvertToGlbBytes(MeshFileEntry entry)
    {
        var document = Parser.Parse(CreateImportRequest(entry));
        return ModelExportService.BuildGlbBytes(document);
    }

    public static MeshExportResult ConvertFile(
        MeshFileEntry entry,
        string outputDir,
        Ps2WorldzoneConverter.WorldzoneTimeOfDay worldzoneTimeOfDay = Ps2WorldzoneConverter.WorldzoneTimeOfDay.All,
        float worldzoneScale = 1f,
        MeshOutputFormat outputFormat = MeshOutputFormat.Glb,
        string? blenderHelperPath = null,
        CancellationToken cancellationToken = default)
    {
        var document = Parser.Parse(CreateImportRequest(entry, worldzoneTimeOfDay, worldzoneScale));
        return ModelExportService.Export(
            document,
            new MeshExportRequest
            {
                OutputDirectory = outputDir,
                OutputStem = document.Name,
                Format = outputFormat,
                BlenderHelperPath = blenderHelperPath,
                WorldzoneTimeOfDay = worldzoneTimeOfDay,
                WorldzoneScale = worldzoneScale,
                CancellationToken = cancellationToken
            });
    }

    private static MeshImportRequest CreateImportRequest(
        MeshFileEntry entry,
        Ps2WorldzoneConverter.WorldzoneTimeOfDay worldzoneTimeOfDay = Ps2WorldzoneConverter.WorldzoneTimeOfDay.All,
        float worldzoneScale = 1f)
    {
        return new MeshImportRequest
        {
            Source = entry.Source,
            FileName = entry.FileName,
            OutputStem = GetOutputStem(entry),
            SourceKind = GetSourceKind(entry),
            Ps2SubFormat = entry.Ps2SubFormat,
            HasPlacedPsxCompanion = entry.HasPlacedPsxCompanion,
            WorldzoneTimeOfDay = worldzoneTimeOfDay,
            WorldzoneScale = worldzoneScale
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
        if (entry.IsPsx) return ModelSourceKind.Psx;
        return ModelSourceKind.Ddm;
    }

    private static string GetOutputStem(MeshFileEntry entry)
    {
        if (entry.IsPs2Scene || entry.IsPs2Geom || entry.IsXbxScene || entry.IsPakWorldzone)
            return MeshConverterTabFileScanner.StripCompoundExtension(entry.FileName);

        var stem = Path.GetFileNameWithoutExtension(entry.FileName);
        if (entry.IsCol && stem.EndsWith(".col", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^4];
        return stem;
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

        var skeBytes = entry.Source.TryReadCompanion(stem + ".ske");
        if (skeBytes != null)
        {
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

    private static MeshNamedTextureResolver? BuildRwTxdTextureProvider(MeshFileEntry entry)
    {
        var stem = Path.GetFileNameWithoutExtension(entry.FileName);
        var texBytes = entry.Source.TryReadCompanion(stem, RwTexExtensions, RwTexSubdirs);
        if (texBytes == null) return null;
        var txdResult = RwTxdFile.Parse(texBytes);
        if (!txdResult.Success) return null;
        return RwDffGltfWriter.BuildTxdTextureProvider(txdResult);
    }

    internal static MeshChecksumTextureResolver? BuildPs2TextureProvider(byte[]? textureBytes)
    {
        if (textureBytes == null) return null;

        var texResult = Ps2TexFile.Parse(textureBytes);
        if (!texResult.Success)
            texResult = ThawSceneTexFile.Parse(textureBytes);
        if (!texResult.Success)
            return null;

        var cache = new Dictionary<uint, Ps2Texture>();
        foreach (var tex in texResult.Textures)
            if (tex.Pixels != null)
                cache.TryAdd(tex.Checksum, tex);

        return checksum =>
        {
            if (!cache.TryGetValue(checksum, out var tex) || tex.Pixels == null)
                return null;
            return ImageWriter.WritePngToMemory(tex.Width, tex.Height, tex.Pixels);
        };
    }
}
