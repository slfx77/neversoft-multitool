using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Collision;
using NeversoftMultitool.Core.Formats.Mesh;
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
using NeversoftMultitool.Core.Formats.XbxScene;
using SharpGLTF.Schema2;

namespace NeversoftMultitool;

internal static class MeshConverterTabFileConverter
{
    internal static readonly string[] Ps2TexExtensions = [".tex.ps2", ".tex", ".img.ps2"];
    internal static readonly string[] Ps2TexSubdirs = ["TEX", "Textures", "IMG"];
    private static readonly string[] XbxTexExtensions = [".tex.xbx", ".tex.wpc"];
    private static readonly string[] XbxTexSubdirs = ["TEX", "Textures"];
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
        if (entry.IsPakWorldzone)
            return (null, 0); // Worldzones produce a full-level .glb; not suitable for preview
        if (entry.IsCol)
            return BuildColGlb(entry);
        if (entry.IsPs2Scene)
            return BuildPs2SceneGlb(entry);
        if (entry.IsPs2Geom)
            return BuildPs2GeomGlb(entry);
        if (entry.IsXbxScene)
            return BuildXbxSceneGlb(entry);
        if (entry.IsRwBsp)
            return BuildRwBspGlb(entry);
        if (entry.IsRwDff)
            return BuildRwDffGlb(entry);
        if (entry.IsPsx)
            return BuildPsxGlb(entry);
        if (entry.IsPlacedLevel)
            return (null, 0); // Placed levels produce multiple files; not supported in preview
        return BuildDdmGlb(entry);
    }

    public static int ConvertFile(
        MeshFileEntry entry,
        string outputDir,
        Ps2WorldzoneConverter.WorldzoneTimeOfDay worldzoneTimeOfDay = Ps2WorldzoneConverter.WorldzoneTimeOfDay.All,
        float worldzoneScale = 1f)
    {
        if (entry.IsPakWorldzone)
            return ConvertPakWorldzone(entry, outputDir, worldzoneTimeOfDay, worldzoneScale);
        if (entry.IsCol)
            return ConvertColFile(entry, outputDir);
        if (entry.IsPs2Scene)
            return ConvertPs2SceneFile(entry, outputDir);
        if (entry.IsPs2Geom)
            return ConvertPs2GeomFile(entry, outputDir);
        if (entry.IsXbxScene)
            return ConvertXbxSceneFile(entry, outputDir);
        if (entry.IsRwBsp)
            return ConvertRwBspFile(entry, outputDir);
        if (entry.IsRwDff)
            return ConvertRwDffFile(entry, outputDir);
        if (entry.IsPsx)
            return ConvertPsxFile(entry, outputDir);
        if (entry.IsPlacedLevel)
            return ConvertPlacedDdm(entry, outputDir);

        return ConvertDdmFile(entry, outputDir);
    }

    private static int ConvertPakWorldzone(
        MeshFileEntry entry,
        string outputDir,
        Ps2WorldzoneConverter.WorldzoneTimeOfDay worldzoneTimeOfDay,
        float worldzoneScale)
    {
        var result = Ps2WorldzoneConverter.Convert(
            entry.Source,
            outputDir,
            new Ps2WorldzoneConverter.WorldzoneOptions(
                Combined: true,
                TimeOfDay: worldzoneTimeOfDay,
                CoordinateScale: worldzoneScale));
        return result.Triangles;
    }

    private static int ConvertRwDffFile(MeshFileEntry entry, string outputDir)
    {
        var clump = RwDffFile.Parse(entry.Source.ReadBytes());
        var textureProvider = BuildRwTxdTextureProvider(entry, forBsp: false);
        var outputFile = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(entry.FileName) + ".glb");
        return RwDffGltfWriter.Write(clump, outputFile, textureProvider);
    }

    private static int ConvertPs2SceneFile(MeshFileEntry entry, string outputDir)
    {
        var data = entry.Source.ReadBytes();
        var stem = MeshConverterTabFileScanner.StripCompoundExtension(entry.FileName);
        var outputPath = Path.Combine(outputDir, stem + ".glb");

        var companionTexData = entry.Source.TryReadCompanion(stem, Ps2TexExtensions, Ps2TexSubdirs);
        var textureProvider = BuildPs2TextureProvider(companionTexData);

        if (entry.Ps2SubFormat == Ps2SceneSubFormat.PakMdl)
        {
            var geomScene = Ps2GeomFile.ParsePakMdl(data);
            return Ps2GeomGltfWriter.Write(geomScene, outputPath, textureProvider);
        }

        var scene = entry.Ps2SubFormat switch
        {
            Ps2SceneSubFormat.ThawSkin => ThawPs2SkinFile.Parse(data, companionTexData),
            Ps2SceneSubFormat.PakSkin => ThawPs2SkinFile.ParsePakSkin(data),
            _ => Ps2SceneFile.Parse(data)
        };

        var skeleton = TryLoadPs2Skeleton(entry, stem);

        if (skeleton != null && entry.Ps2SubFormat == Ps2SceneSubFormat.ThawSkin)
        {
            var pcBytes = entry.Source.TryReadCompanion(stem, PcSkinExtensions, PcSkinSubdirs);
            var transferred = pcBytes != null
                ? ThawPs2SkinningTransfer.TryApplyFromBytes(scene, pcBytes, skeleton)
                : null;
            if (transferred is { SkinnedVertexCount: > 0 })
                scene = transferred.Scene;
            else
                skeleton = null;
        }

        return skeleton != null
            ? Ps2SceneGltfWriter.WriteSkinned(scene, skeleton, outputPath, textureProvider)
            : Ps2SceneGltfWriter.Write(scene, outputPath, textureProvider);
    }

    private static int ConvertPs2GeomFile(MeshFileEntry entry, string outputDir)
    {
        var scene = Ps2GeomFile.Parse(entry.Source.ReadBytes());
        var stem = MeshConverterTabFileScanner.StripCompoundExtension(entry.FileName);
        var outputPath = Path.Combine(outputDir, stem + ".glb");
        var companionTexData = entry.Source.TryReadCompanion(stem, Ps2TexExtensions, Ps2TexSubdirs);
        var textureProvider = BuildPs2TextureProvider(companionTexData);
        return Ps2GeomGltfWriter.Write(scene, outputPath, textureProvider);
    }

    private static int ConvertXbxSceneFile(MeshFileEntry entry, string outputDir)
    {
        var data = entry.Source.ReadBytes();
        var stem = MeshConverterTabFileScanner.StripCompoundExtension(entry.FileName);
        var outputPath = Path.Combine(outputDir, stem + ".glb");

        var scene = ThawSceneFile.IsThawScene(data)
            ? ThawSceneFile.Parse(data)
            : XbxSceneFile.Parse(data);

        var textureProvider = BuildXbxSceneTextureProvider(entry, stem);
        return XbxSceneGltfWriter.Write(scene, outputPath, textureProvider);
    }

    private static int ConvertColFile(MeshFileEntry entry, string outputDir)
    {
        var scene = ColFile.Parse(entry.Source.ReadBytes());
        var stem = Path.GetFileNameWithoutExtension(entry.FileName);
        if (stem.EndsWith(".col", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^4];
        var outputFile = Path.Combine(outputDir, stem + ".glb");
        return ColGltfWriter.Write(scene, outputFile);
    }

    private static int ConvertRwBspFile(MeshFileEntry entry, string outputDir)
    {
        var world = RwBspFile.Parse(entry.Source.ReadBytes());
        var textureProvider = BuildRwTxdTextureProvider(entry, forBsp: true);
        var outputFile = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(entry.FileName) + ".glb");
        return RwBspGltfWriter.Write(world, outputFile, textureProvider);
    }

    private static int ConvertPsxFile(MeshFileEntry entry, string outputDir)
    {
        var psxData = entry.Source.ReadBytes();
        var psxFile = PsxMeshFile.Parse(psxData)
                      ?? throw new InvalidOperationException("No mesh data");

        var textureProvider = BuildPsxTextureProvider(entry, psxData);

        PshFile? pshFile = null;
        if (psxFile.HasHierarchy)
        {
            var stem = Path.GetFileNameWithoutExtension(entry.FileName);
            var pshBytes = entry.Source.TryReadCompanion(stem + ".psh");
            pshFile = pshBytes != null ? PshFile.Parse(pshBytes) : null;
        }

        var outputFile = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(entry.FileName) + ".glb");
        return PsxGltfWriter.Write(psxFile, outputFile, textureProvider, pshFile);
    }

    private static int ConvertDdmFile(MeshFileEntry entry, string outputDir)
    {
        var ddm = DdmFile.Parse(entry.Source.ReadBytes());
        var ddmName = Path.GetFileNameWithoutExtension(entry.FileName);
        var outputFile = Path.Combine(outputDir, ddmName + ".glb");

        var ddxTextures = LoadDdxCompanion(entry, ddmName);
        var lights = LoadLitCompanion(entry, ddmName);

        return GltfWriter.WriteDdm(ddm, outputFile, null, ddmName, ddxTextures, lights);
    }

    private static int ConvertPlacedDdm(MeshFileEntry entry, string outputDir)
    {
        // Placed-level conversion is deeply filesystem-based (multiple companion paths).
        // For archive sources we don't support placed-level; the scanner should have
        // flagged HasPlacedPsxCompanion=false in that case, but guard anyway.
        var ddmPath = entry.Source.FileSystemPath;
        if (ddmPath == null)
            return ConvertDdmFile(entry, outputDir);

        var ddmName = Path.GetFileNameWithoutExtension(entry.FileName);
        var inputDir = Path.GetDirectoryName(ddmPath)!;
        var companionPsx = entry.Source.TryResolveCompanionPath(ddmName + ".psx");
        if (companionPsx == null)
            return ConvertDdmFile(entry, outputDir);

        var companionObjectsDdm = entry.Source.TryResolveCompanionPath(ddmName + "_o.ddm");
        var objectsPsx = companionObjectsDdm != null
            ? entry.Source.TryResolveCompanionPath(ddmName + "_o.psx")
            : null;

        var (levelTriangles, objectTriangles) = GltfWriter.WritePlacedLevel(
            ddmPath,
            companionPsx,
            companionObjectsDdm,
            objectsPsx,
            outputDir,
            ddmName,
            inputDir);

        return levelTriangles + objectTriangles;
    }

    private static byte[]? WriteGlbToMemory(ModelRoot model)
    {
        using var ms = new MemoryStream();
        model.WriteGLB(ms);
        return ms.ToArray();
    }

    private static (byte[]? GlbBytes, int Triangles) BuildColGlb(MeshFileEntry entry)
    {
        var scene = ColFile.Parse(entry.Source.ReadBytes());
        var (model, triangles) = ColGltfWriter.Build(scene);
        return triangles > 0 ? (WriteGlbToMemory(model), triangles) : (null, 0);
    }

    private static (byte[]? GlbBytes, int Triangles) BuildPs2SceneGlb(MeshFileEntry entry)
    {
        var data = entry.Source.ReadBytes();
        var stem = MeshConverterTabFileScanner.StripCompoundExtension(entry.FileName);
        var companionTexData = entry.Source.TryReadCompanion(stem, Ps2TexExtensions, Ps2TexSubdirs);
        var textureProvider = BuildPs2TextureProvider(companionTexData);

        if (entry.Ps2SubFormat == Ps2SceneSubFormat.PakMdl)
        {
            var geomScene = Ps2GeomFile.ParsePakMdl(data);
            var (model, triangles) = Ps2GeomGltfWriter.Build(geomScene, textureProvider);
            return triangles > 0 ? (WriteGlbToMemory(model), triangles) : (null, 0);
        }

        var scene = entry.Ps2SubFormat switch
        {
            Ps2SceneSubFormat.ThawSkin => ThawPs2SkinFile.Parse(data, companionTexData),
            Ps2SceneSubFormat.PakSkin => ThawPs2SkinFile.ParsePakSkin(data),
            _ => Ps2SceneFile.Parse(data)
        };

        var skeleton = TryLoadPs2Skeleton(entry, stem);

        if (skeleton != null && entry.Ps2SubFormat == Ps2SceneSubFormat.ThawSkin)
        {
            var pcBytes = entry.Source.TryReadCompanion(stem, PcSkinExtensions, PcSkinSubdirs);
            var transferred = pcBytes != null
                ? ThawPs2SkinningTransfer.TryApplyFromBytes(scene, pcBytes, skeleton)
                : null;
            if (transferred is { SkinnedVertexCount: > 0 })
                scene = transferred.Scene;
            else
                skeleton = null;
        }

        var (m, t) = skeleton != null
            ? Ps2SceneGltfWriter.BuildSkinned(scene, skeleton, textureProvider)
            : Ps2SceneGltfWriter.Build(scene, textureProvider);
        return t > 0 ? (WriteGlbToMemory(m), t) : (null, 0);
    }

    private static (byte[]? GlbBytes, int Triangles) BuildPs2GeomGlb(MeshFileEntry entry)
    {
        var scene = Ps2GeomFile.Parse(entry.Source.ReadBytes());
        var stem = MeshConverterTabFileScanner.StripCompoundExtension(entry.FileName);
        var companionTexData = entry.Source.TryReadCompanion(stem, Ps2TexExtensions, Ps2TexSubdirs);
        var textureProvider = BuildPs2TextureProvider(companionTexData);
        var (model, triangles) = Ps2GeomGltfWriter.Build(scene, textureProvider);
        return triangles > 0 ? (WriteGlbToMemory(model), triangles) : (null, 0);
    }

    private static (byte[]? GlbBytes, int Triangles) BuildXbxSceneGlb(MeshFileEntry entry)
    {
        var data = entry.Source.ReadBytes();
        var scene = ThawSceneFile.IsThawScene(data)
            ? ThawSceneFile.Parse(data)
            : XbxSceneFile.Parse(data);

        var stem = MeshConverterTabFileScanner.StripCompoundExtension(entry.FileName);
        var textureProvider = BuildXbxSceneTextureProvider(entry, stem);

        var (model, triangles) = XbxSceneGltfWriter.Build(scene, textureProvider);
        return triangles > 0 ? (WriteGlbToMemory(model), triangles) : (null, 0);
    }

    private static (byte[]? GlbBytes, int Triangles) BuildRwBspGlb(MeshFileEntry entry)
    {
        var world = RwBspFile.Parse(entry.Source.ReadBytes());
        var textureProvider = BuildRwTxdTextureProvider(entry, forBsp: true);
        var (model, triangles) = RwBspGltfWriter.Build(world, textureProvider);
        return triangles > 0 ? (WriteGlbToMemory(model), triangles) : (null, 0);
    }

    private static (byte[]? GlbBytes, int Triangles) BuildRwDffGlb(MeshFileEntry entry)
    {
        var clump = RwDffFile.Parse(entry.Source.ReadBytes());
        var textureProvider = BuildRwTxdTextureProvider(entry, forBsp: false);
        var (model, triangles) = RwDffGltfWriter.Build(clump, textureProvider);
        return triangles > 0 ? (WriteGlbToMemory(model), triangles) : (null, 0);
    }

    private static (byte[]? GlbBytes, int Triangles) BuildPsxGlb(MeshFileEntry entry)
    {
        var psxData = entry.Source.ReadBytes();
        var psxFile = PsxMeshFile.Parse(psxData)
                      ?? throw new InvalidOperationException("No mesh data");

        var textureProvider = BuildPsxTextureProvider(entry, psxData);

        PshFile? pshFile = null;
        if (psxFile.HasHierarchy)
        {
            var stem = Path.GetFileNameWithoutExtension(entry.FileName);
            var pshBytes = entry.Source.TryReadCompanion(stem + ".psh");
            pshFile = pshBytes != null ? PshFile.Parse(pshBytes) : null;
        }

        var (model, triangles) = PsxGltfWriter.Build(psxFile, textureProvider, pshFile);
        return triangles > 0 ? (WriteGlbToMemory(model), triangles) : (null, 0);
    }

    private static (byte[]? GlbBytes, int Triangles) BuildDdmGlb(MeshFileEntry entry)
    {
        var ddm = DdmFile.Parse(entry.Source.ReadBytes());
        var ddmName = Path.GetFileNameWithoutExtension(entry.FileName);

        var ddxTextures = LoadDdxCompanion(entry, ddmName);
        var lights = LoadLitCompanion(entry, ddmName);

        var (model, triangles) = GltfWriter.BuildDdmModel(ddm, null, ddmName, ddxTextures, lights);
        return triangles > 0 ? (WriteGlbToMemory(model), triangles) : (null, 0);
    }

    internal static Ps2Skeleton? TryLoadPs2Skeleton(MeshFileEntry entry, string stem)
    {
        // Prefer exact companion (.ske.ps2 then .ske). Scanner may have pre-resolved
        // a skeleton from a filesystem-wide index (ThawSkeletonDiscovery); reuse by
        // re-reading via Source so the parse works uniformly for both backings.
        var ps2Bytes = entry.Source.TryReadCompanion(stem + ".ske.ps2");
        if (ps2Bytes != null)
        {
            try { return Ps2SkeletonFile.Parse(ps2Bytes); } catch { /* fall through */ }
        }

        var skeBytes = entry.Source.TryReadCompanion(stem + ".ske");
        if (skeBytes != null)
        {
            try { return SkeletonFile.Parse(skeBytes); } catch { /* fall through */ }
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
                catch { /* proceed without skeleton */ }
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
                catch { /* proceed without skeleton */ }
            }
        }

        return null;
    }

    private static Dictionary<string, byte[]>? LoadDdxCompanion(MeshFileEntry entry, string ddmName)
    {
        var ddxBytes = entry.Source.TryReadCompanion(ddmName + ".ddx");
        return ddxBytes != null ? DdxArchive.ReadAllEntries(ddxBytes) : null;
    }

    private static List<LitLight>? LoadLitCompanion(MeshFileEntry entry, string ddmName)
    {
        var litBytes = entry.Source.TryReadCompanion(ddmName + ".lit");
        if (litBytes == null) return null;
        try
        {
            return LitFile.Parse(litBytes);
        }
        catch
        {
            return null;
        }
    }

    internal static RwDffGltfWriter.TextureProvider? BuildRwDffTextureProvider(MeshFileEntry entry)
        => BuildRwTxdTextureProvider(entry, forBsp: false);

    private static RwDffGltfWriter.TextureProvider? BuildRwTxdTextureProvider(MeshFileEntry entry, bool forBsp)
    {
        var stem = Path.GetFileNameWithoutExtension(entry.FileName);
        var texBytes = entry.Source.TryReadCompanion(stem, RwTexExtensions, RwTexSubdirs);
        if (texBytes == null) return null;
        var txdResult = RwTxdFile.Parse(texBytes);
        if (!txdResult.Success) return null;
        return forBsp
            ? RwBspGltfWriter.BuildTxdTextureProvider(txdResult)
            : RwDffGltfWriter.BuildTxdTextureProvider(txdResult);
    }

    private static XbxSceneGltfWriter.TextureProvider? BuildXbxSceneTextureProvider(MeshFileEntry entry, string stem)
    {
        var texBytes = entry.Source.TryReadCompanion(stem, XbxTexExtensions, XbxTexSubdirs);
        if (texBytes == null) return null;

        var texResult = XbxTexFile.Parse(texBytes);
        if (!texResult.Success)
            texResult = ThawTexFile.Parse(texBytes);
        if (!texResult.Success) return null;

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

    private static PsxGltfWriter.TextureProvider BuildPsxTextureProvider(MeshFileEntry entry, byte[] psxData)
    {
        // PSX textures live in (a) the PSX file itself, or (b) a companion *_l.psx
        // library (character models with separate texture libraries). For archive
        // sources the library is resolved as a sibling entry via Source; the
        // filesystem code path reads the library from disk.
        var meshLabel = entry.FileName;
        var stem = Path.GetFileNameWithoutExtension(entry.FileName);

        byte[]? libraryBytes = null;
        string libraryLabel = "";
        if (stem.EndsWith("_g", StringComparison.OrdinalIgnoreCase))
        {
            var libraryName = stem[..^2] + "_l.psx";
            libraryBytes = entry.Source.TryReadCompanion(libraryName);
            libraryLabel = libraryName;
        }

        return hash =>
        {
            var result = PsxLibrary.ExtractTextureByHash(psxData, hash, meshLabel);
            if (result == null && libraryBytes != null)
                result = PsxLibrary.ExtractTextureByHash(libraryBytes, hash, libraryLabel);
            if (result == null)
                return null;
            var (rgba, width, height) = result.Value;
            return ImageWriter.WritePngToMemory(width, height, rgba);
        };
    }

    internal static Ps2SceneGltfWriter.TextureProvider? BuildPs2TextureProvider(byte[]? textureBytes)
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
