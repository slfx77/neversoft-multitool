using NeversoftMultitool.Core;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Collision;
using NeversoftMultitool.Core.Formats.Mesh.Ddm;
using NeversoftMultitool.Core.Formats.Mesh.Lit;
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
    /// <summary>
    ///     Converts a mesh file to GLB bytes in memory (no temp files).
    ///     Used by the preview panel for on-select 3D viewing.
    /// </summary>
    public static (byte[]? GlbBytes, int Triangles) ConvertToGlbBytes(MeshFileEntry entry)
    {
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

    public static int ConvertFile(MeshFileEntry entry, string outputDir)
    {
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

    private static int ConvertRwDffFile(MeshFileEntry entry, string outputDir)
    {
        var clump = RwDffFile.Parse(entry.FilePath);

        RwDffGltfWriter.TextureProvider? textureProvider = null;
        var directory = Path.GetDirectoryName(entry.FilePath)!;
        var stem = Path.GetFileNameWithoutExtension(entry.FileName);
        var texFile = CompanionSearch.FindCompanion(directory, stem, [".tex"], ["TEX", "Textures"]);
        if (texFile != null)
        {
            var txdResult = RwTxdFile.Parse(texFile);
            if (txdResult.Success)
                textureProvider = RwDffGltfWriter.BuildTxdTextureProvider(txdResult);
        }

        var outputFile = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(entry.FileName) + ".glb");
        return RwDffGltfWriter.Write(clump, outputFile, textureProvider);
    }

    private static int ConvertPs2SceneFile(MeshFileEntry entry, string outputDir)
    {
        var data = File.ReadAllBytes(entry.FilePath);
        var stem = MeshConverterTabFileScanner.StripCompoundExtension(entry.FileName);
        var outputPath = Path.Combine(outputDir, stem + ".glb");
        byte[]? companionTexData = null;
        if (entry.CompanionTexPath != null && File.Exists(entry.CompanionTexPath))
            companionTexData = File.ReadAllBytes(entry.CompanionTexPath);

        var textureProvider = BuildPs2TextureProvider(entry.CompanionTexPath);

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

        Ps2Skeleton? skeleton = null;
        if (entry.CompanionSkeletonPath != null)
        {
            try
            {
                skeleton = entry.CompanionSkeletonPath.EndsWith(".ske.ps2", StringComparison.OrdinalIgnoreCase)
                    ? Ps2SkeletonFile.Parse(entry.CompanionSkeletonPath)
                    : SkeletonFile.Parse(entry.CompanionSkeletonPath);
            }
            catch
            {
                /* proceed without skeleton */
            }
        }

        if (skeleton != null && entry.Ps2SubFormat == Ps2SceneSubFormat.ThawSkin)
        {
            var transferred = ThawPs2SkinningTransfer.TryApplyFromCompanion(scene, entry.FilePath, skeleton);
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
        var scene = Ps2GeomFile.Parse(entry.FilePath);
        var stem = MeshConverterTabFileScanner.StripCompoundExtension(entry.FileName);
        var outputPath = Path.Combine(outputDir, stem + ".glb");
        var textureProvider = BuildPs2TextureProvider(entry.CompanionTexPath);
        return Ps2GeomGltfWriter.Write(scene, outputPath, textureProvider);
    }

    private static int ConvertXbxSceneFile(MeshFileEntry entry, string outputDir)
    {
        var data = File.ReadAllBytes(entry.FilePath);
        var stem = MeshConverterTabFileScanner.StripCompoundExtension(entry.FileName);
        var outputPath = Path.Combine(outputDir, stem + ".glb");

        var scene = ThawSceneFile.IsThawScene(data)
            ? ThawSceneFile.Parse(data)
            : XbxSceneFile.Parse(data);

        XbxSceneGltfWriter.TextureProvider? textureProvider = null;
        if (entry.CompanionTexPath != null)
        {
            var texResult = XbxTexFile.Parse(entry.CompanionTexPath);
            if (!texResult.Success)
                texResult = ThawTexFile.Parse(entry.CompanionTexPath);
            if (texResult.Success)
            {
                var cache = new Dictionary<uint, Ps2Texture>();
                foreach (var tex in texResult.Textures)
                    if (tex.Pixels != null)
                        cache.TryAdd(tex.Checksum, tex);
                textureProvider = checksum =>
                {
                    if (!cache.TryGetValue(checksum, out var tex) || tex.Pixels == null)
                        return null;
                    return ImageWriter.WritePngToMemory(tex.Width, tex.Height, tex.Pixels);
                };
            }
        }

        return XbxSceneGltfWriter.Write(scene, outputPath, textureProvider);
    }

    private static int ConvertColFile(MeshFileEntry entry, string outputDir)
    {
        var scene = ColFile.Parse(entry.FilePath);
        var stem = Path.GetFileNameWithoutExtension(entry.FileName);
        if (stem.EndsWith(".col", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^4];
        var outputFile = Path.Combine(outputDir, stem + ".glb");
        return ColGltfWriter.Write(scene, outputFile);
    }

    private static int ConvertRwBspFile(MeshFileEntry entry, string outputDir)
    {
        var world = RwBspFile.Parse(entry.FilePath);

        RwDffGltfWriter.TextureProvider? textureProvider = null;
        var directory = Path.GetDirectoryName(entry.FilePath)!;
        var stem = Path.GetFileNameWithoutExtension(entry.FileName);
        var texFile = CompanionSearch.FindCompanion(directory, stem, [".tex"], ["TEX", "Textures"]);
        if (texFile != null)
        {
            var txdResult = RwTxdFile.Parse(texFile);
            if (txdResult.Success)
                textureProvider = RwBspGltfWriter.BuildTxdTextureProvider(txdResult);
        }

        var outputFile = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(entry.FileName) + ".glb");
        return RwBspGltfWriter.Write(world, outputFile, textureProvider);
    }

    private static int ConvertPsxFile(MeshFileEntry entry, string outputDir)
    {
        var psxFile = PsxMeshFile.Parse(entry.FilePath)
                      ?? throw new InvalidOperationException("No mesh data");

        var filePath = entry.FilePath;
        var companionLibraryPath = entry.CompanionLibraryPsxPath;
        PsxGltfWriter.TextureProvider textureProvider = hash =>
        {
            var result = PsxLibrary.ExtractTextureByHash(filePath, hash);
            if (result == null && companionLibraryPath != null)
                result = PsxLibrary.ExtractTextureByHash(companionLibraryPath, hash);
            if (result == null)
                return null;
            var (rgba, width, height) = result.Value;
            return ImageWriter.WritePngToMemory(width, height, rgba);
        };

        var pshFile = psxFile.HasHierarchy ? PshFile.FindCompanion(entry.FilePath) : null;
        var outputFile = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(entry.FileName) + ".glb");
        return PsxGltfWriter.Write(psxFile, outputFile, textureProvider, pshFile);
    }

    private static int ConvertDdmFile(MeshFileEntry entry, string outputDir)
    {
        var ddm = DdmFile.Parse(entry.FilePath);
        var ddmName = Path.GetFileNameWithoutExtension(entry.FileName);
        var inputDir = Path.GetDirectoryName(entry.FilePath)!;
        var outputFile = Path.Combine(outputDir, ddmName + ".glb");

        Dictionary<string, byte[]>? ddxTextures = null;
        var ddxFile = MeshConverterTabFileScanner.FindCompanionFile(inputDir, ddmName, ".ddx");
        if (ddxFile != null)
            ddxTextures = DdxArchive.ReadAllEntries(ddxFile);

        List<LitLight>? lights = null;
        var litFile = MeshConverterTabFileScanner.FindCompanionFile(inputDir, ddmName, ".lit");
        if (litFile != null)
        {
            try
            {
                lights = LitFile.Parse(litFile);
            }
            catch
            {
                /* ignore parse errors */
            }
        }

        return GltfWriter.WriteDdm(ddm, outputFile, null, ddmName, ddxTextures, lights);
    }

    private static int ConvertPlacedDdm(MeshFileEntry entry, string outputDir)
    {
        var ddmName = Path.GetFileNameWithoutExtension(entry.FileName);
        var inputDir = Path.GetDirectoryName(entry.FilePath)!;
        var objectsPsx = entry.CompanionObjectsDdmPath != null
            ? MeshConverterTabFileScanner.FindCompanionFile(inputDir, ddmName + "_o", ".psx")
            : null;

        var (levelTriangles, objectTriangles) = GltfWriter.WritePlacedLevel(
            entry.FilePath,
            entry.CompanionPsxPath!,
            entry.CompanionObjectsDdmPath,
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
        var scene = ColFile.Parse(entry.FilePath);
        var (model, triangles) = ColGltfWriter.Build(scene);
        return triangles > 0 ? (WriteGlbToMemory(model), triangles) : (null, 0);
    }

    private static (byte[]? GlbBytes, int Triangles) BuildPs2SceneGlb(MeshFileEntry entry)
    {
        var data = File.ReadAllBytes(entry.FilePath);
        byte[]? companionTexData = null;
        if (entry.CompanionTexPath != null && File.Exists(entry.CompanionTexPath))
            companionTexData = File.ReadAllBytes(entry.CompanionTexPath);

        var textureProvider = BuildPs2TextureProvider(entry.CompanionTexPath);

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

        Ps2Skeleton? skeleton = null;
        if (entry.CompanionSkeletonPath != null)
        {
            try
            {
                skeleton = entry.CompanionSkeletonPath.EndsWith(".ske.ps2", StringComparison.OrdinalIgnoreCase)
                    ? Ps2SkeletonFile.Parse(entry.CompanionSkeletonPath)
                    : SkeletonFile.Parse(entry.CompanionSkeletonPath);
            }
            catch
            {
                /* proceed without skeleton */
            }
        }

        if (skeleton != null && entry.Ps2SubFormat == Ps2SceneSubFormat.ThawSkin)
        {
            var transferred = ThawPs2SkinningTransfer.TryApplyFromCompanion(scene, entry.FilePath, skeleton);
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
        var scene = Ps2GeomFile.Parse(entry.FilePath);
        var textureProvider = BuildPs2TextureProvider(entry.CompanionTexPath);
        var (model, triangles) = Ps2GeomGltfWriter.Build(scene, textureProvider);
        return triangles > 0 ? (WriteGlbToMemory(model), triangles) : (null, 0);
    }

    private static (byte[]? GlbBytes, int Triangles) BuildXbxSceneGlb(MeshFileEntry entry)
    {
        var data = File.ReadAllBytes(entry.FilePath);
        var scene = ThawSceneFile.IsThawScene(data)
            ? ThawSceneFile.Parse(data)
            : XbxSceneFile.Parse(data);

        XbxSceneGltfWriter.TextureProvider? textureProvider = null;
        if (entry.CompanionTexPath != null)
        {
            var texResult = XbxTexFile.Parse(entry.CompanionTexPath);
            if (!texResult.Success)
                texResult = ThawTexFile.Parse(entry.CompanionTexPath);
            if (texResult.Success)
            {
                var cache = new Dictionary<uint, Ps2Texture>();
                foreach (var tex in texResult.Textures)
                    if (tex.Pixels != null)
                        cache.TryAdd(tex.Checksum, tex);
                textureProvider = checksum =>
                {
                    if (!cache.TryGetValue(checksum, out var tex) || tex.Pixels == null)
                        return null;
                    return ImageWriter.WritePngToMemory(tex.Width, tex.Height, tex.Pixels);
                };
            }
        }

        var (model, triangles) = XbxSceneGltfWriter.Build(scene, textureProvider);
        return triangles > 0 ? (WriteGlbToMemory(model), triangles) : (null, 0);
    }

    private static (byte[]? GlbBytes, int Triangles) BuildRwBspGlb(MeshFileEntry entry)
    {
        var world = RwBspFile.Parse(entry.FilePath);
        RwDffGltfWriter.TextureProvider? textureProvider = null;
        var directory = Path.GetDirectoryName(entry.FilePath)!;
        var stem = Path.GetFileNameWithoutExtension(entry.FileName);
        var texFile = CompanionSearch.FindCompanion(directory, stem, [".tex"], ["TEX", "Textures"]);
        if (texFile != null)
        {
            var txdResult = RwTxdFile.Parse(texFile);
            if (txdResult.Success)
                textureProvider = RwBspGltfWriter.BuildTxdTextureProvider(txdResult);
        }

        var (model, triangles) = RwBspGltfWriter.Build(world, textureProvider);
        return triangles > 0 ? (WriteGlbToMemory(model), triangles) : (null, 0);
    }

    private static (byte[]? GlbBytes, int Triangles) BuildRwDffGlb(MeshFileEntry entry)
    {
        var clump = RwDffFile.Parse(entry.FilePath);
        RwDffGltfWriter.TextureProvider? textureProvider = null;
        var directory = Path.GetDirectoryName(entry.FilePath)!;
        var stem = Path.GetFileNameWithoutExtension(entry.FileName);
        var texFile = CompanionSearch.FindCompanion(directory, stem, [".tex"], ["TEX", "Textures"]);
        if (texFile != null)
        {
            var txdResult = RwTxdFile.Parse(texFile);
            if (txdResult.Success)
                textureProvider = RwDffGltfWriter.BuildTxdTextureProvider(txdResult);
        }

        var (model, triangles) = RwDffGltfWriter.Build(clump, textureProvider);
        return triangles > 0 ? (WriteGlbToMemory(model), triangles) : (null, 0);
    }

    private static (byte[]? GlbBytes, int Triangles) BuildPsxGlb(MeshFileEntry entry)
    {
        var psxFile = PsxMeshFile.Parse(entry.FilePath)
                      ?? throw new InvalidOperationException("No mesh data");

        var filePath = entry.FilePath;
        var companionLibraryPath = entry.CompanionLibraryPsxPath;
        PsxGltfWriter.TextureProvider textureProvider = hash =>
        {
            var result = PsxLibrary.ExtractTextureByHash(filePath, hash);
            if (result == null && companionLibraryPath != null)
                result = PsxLibrary.ExtractTextureByHash(companionLibraryPath, hash);
            if (result == null)
                return null;
            var (rgba, width, height) = result.Value;
            return ImageWriter.WritePngToMemory(width, height, rgba);
        };

        var pshFile = psxFile.HasHierarchy ? PshFile.FindCompanion(entry.FilePath) : null;
        var (model, triangles) = PsxGltfWriter.Build(psxFile, textureProvider, pshFile);
        return triangles > 0 ? (WriteGlbToMemory(model), triangles) : (null, 0);
    }

    private static (byte[]? GlbBytes, int Triangles) BuildDdmGlb(MeshFileEntry entry)
    {
        var ddm = DdmFile.Parse(entry.FilePath);
        var ddmName = Path.GetFileNameWithoutExtension(entry.FileName);
        var inputDir = Path.GetDirectoryName(entry.FilePath)!;

        Dictionary<string, byte[]>? ddxTextures = null;
        var ddxFile = MeshConverterTabFileScanner.FindCompanionFile(inputDir, ddmName, ".ddx");
        if (ddxFile != null)
            ddxTextures = DdxArchive.ReadAllEntries(ddxFile);

        List<LitLight>? lights = null;
        var litFile = MeshConverterTabFileScanner.FindCompanionFile(inputDir, ddmName, ".lit");
        if (litFile != null)
        {
            try
            {
                lights = LitFile.Parse(litFile);
            }
            catch
            {
                /* ignore */
            }
        }

        var (model, triangles) = GltfWriter.BuildDdmModel(ddm, null, ddmName, ddxTextures, lights);
        return triangles > 0 ? (WriteGlbToMemory(model), triangles) : (null, 0);
    }

    private static Ps2SceneGltfWriter.TextureProvider? BuildPs2TextureProvider(string? texturePath)
    {
        if (texturePath == null)
            return null;

        var texResult = Ps2TexFile.Parse(texturePath);
        if (!texResult.Success)
            texResult = ThawSceneTexFile.Parse(texturePath);
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
