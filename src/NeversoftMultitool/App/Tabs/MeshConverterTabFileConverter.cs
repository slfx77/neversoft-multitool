using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Collision;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Ps2Scene;
using NeversoftMultitool.Core.Formats.Psx;
using NeversoftMultitool.Core.Formats.XbxScene;

namespace NeversoftMultitool;

internal static class MeshConverterTabFileConverter
{
    public static int ConvertFile(MeshFileEntry entry, string outputDir, bool embedTextures)
    {
        if (entry.IsCol)
            return ConvertColFile(entry, outputDir);
        if (entry.IsPs2Scene)
            return ConvertPs2SceneFile(entry, outputDir, embedTextures);
        if (entry.IsPs2Geom)
            return ConvertPs2GeomFile(entry, outputDir, embedTextures);
        if (entry.IsXbxScene)
            return ConvertXbxSceneFile(entry, outputDir, embedTextures);
        if (entry.IsRwBsp)
            return ConvertRwBspFile(entry, outputDir, embedTextures);
        if (entry.IsRwDff)
            return ConvertRwDffFile(entry, outputDir, embedTextures);
        if (entry.IsPsx)
            return ConvertPsxFile(entry, outputDir, embedTextures);
        if (entry.IsPlacedLevel)
            return ConvertPlacedDdm(entry, outputDir, embedTextures);

        return ConvertDdmFile(entry, outputDir, embedTextures);
    }

    private static int ConvertRwDffFile(MeshFileEntry entry, string outputDir, bool embedTextures)
    {
        var clump = RwDffFile.Parse(entry.FilePath);

        RwDffGltfWriter.TextureProvider? textureProvider = null;
        if (embedTextures)
        {
            var directory = Path.GetDirectoryName(entry.FilePath)!;
            var stem = Path.GetFileNameWithoutExtension(entry.FileName);
            var texFile = CompanionSearch.FindCompanion(directory, stem, [".tex"], ["TEX", "Textures"]);
            if (texFile != null)
            {
                var txdResult = RwTxdFile.Parse(texFile);
                if (txdResult.Success)
                    textureProvider = RwDffGltfWriter.BuildTxdTextureProvider(txdResult);
            }
        }

        var outputFile = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(entry.FileName) + ".glb");
        return RwDffGltfWriter.Write(clump, outputFile, textureProvider);
    }

    private static int ConvertPs2SceneFile(MeshFileEntry entry, string outputDir, bool embedTextures)
    {
        var data = File.ReadAllBytes(entry.FilePath);
        var stem = MeshConverterTabFileScanner.StripCompoundExtension(entry.FileName);
        var outputPath = Path.Combine(outputDir, stem + ".glb");
        byte[]? companionTexData = null;
        if (entry.CompanionTexPath != null && File.Exists(entry.CompanionTexPath))
            companionTexData = File.ReadAllBytes(entry.CompanionTexPath);

        var textureProvider = BuildPs2TextureProvider(entry.CompanionTexPath, embedTextures);

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

    private static int ConvertPs2GeomFile(MeshFileEntry entry, string outputDir, bool embedTextures)
    {
        var scene = Ps2GeomFile.Parse(entry.FilePath);
        var stem = MeshConverterTabFileScanner.StripCompoundExtension(entry.FileName);
        var outputPath = Path.Combine(outputDir, stem + ".glb");
        var textureProvider = BuildPs2TextureProvider(entry.CompanionTexPath, embedTextures);
        return Ps2GeomGltfWriter.Write(scene, outputPath, textureProvider);
    }

    private static int ConvertXbxSceneFile(MeshFileEntry entry, string outputDir, bool embedTextures)
    {
        var data = File.ReadAllBytes(entry.FilePath);
        var stem = MeshConverterTabFileScanner.StripCompoundExtension(entry.FileName);
        var outputPath = Path.Combine(outputDir, stem + ".glb");

        var scene = ThawSceneFile.IsThawScene(data)
            ? ThawSceneFile.Parse(data)
            : XbxSceneFile.Parse(data);

        XbxSceneGltfWriter.TextureProvider? textureProvider = null;
        if (embedTextures && entry.CompanionTexPath != null)
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

    private static int ConvertRwBspFile(MeshFileEntry entry, string outputDir, bool embedTextures)
    {
        var world = RwBspFile.Parse(entry.FilePath);

        RwDffGltfWriter.TextureProvider? textureProvider = null;
        if (embedTextures)
        {
            var directory = Path.GetDirectoryName(entry.FilePath)!;
            var stem = Path.GetFileNameWithoutExtension(entry.FileName);
            var texFile = CompanionSearch.FindCompanion(directory, stem, [".tex"], ["TEX", "Textures"]);
            if (texFile != null)
            {
                var txdResult = RwTxdFile.Parse(texFile);
                if (txdResult.Success)
                    textureProvider = RwBspGltfWriter.BuildTxdTextureProvider(txdResult);
            }
        }

        var outputFile = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(entry.FileName) + ".glb");
        return RwBspGltfWriter.Write(world, outputFile, textureProvider);
    }

    private static int ConvertPsxFile(MeshFileEntry entry, string outputDir, bool embedTextures)
    {
        var psxFile = PsxMeshFile.Parse(entry.FilePath)
                      ?? throw new InvalidOperationException("No mesh data");

        PsxGltfWriter.TextureProvider? textureProvider = null;
        if (embedTextures)
        {
            var filePath = entry.FilePath;
            var companionLibraryPath = entry.CompanionLibraryPsxPath;
            textureProvider = hash =>
            {
                var result = PsxLibrary.ExtractTextureByHash(filePath, hash);
                if (result == null && companionLibraryPath != null)
                    result = PsxLibrary.ExtractTextureByHash(companionLibraryPath, hash);
                if (result == null)
                    return null;
                var (rgba, width, height) = result.Value;
                return ImageWriter.WritePngToMemory(width, height, rgba);
            };
        }

        var pshFile = psxFile.HasHierarchy ? PshFile.FindCompanion(entry.FilePath) : null;
        var outputFile = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(entry.FileName) + ".glb");
        return PsxGltfWriter.Write(psxFile, outputFile, textureProvider, pshFile);
    }

    private static int ConvertDdmFile(MeshFileEntry entry, string outputDir, bool embedTextures)
    {
        var ddm = DdmFile.Parse(entry.FilePath);
        var ddmName = Path.GetFileNameWithoutExtension(entry.FileName);
        var inputDir = Path.GetDirectoryName(entry.FilePath)!;
        var outputFile = Path.Combine(outputDir, ddmName + ".glb");

        Dictionary<string, byte[]>? ddxTextures = null;
        if (embedTextures)
        {
            var ddxFile = MeshConverterTabFileScanner.FindCompanionFile(inputDir, ddmName, ".ddx");
            if (ddxFile != null)
                ddxTextures = DdxArchive.ReadAllEntries(ddxFile);
        }

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

    private static int ConvertPlacedDdm(MeshFileEntry entry, string outputDir, bool embedTextures)
    {
        var ddmName = Path.GetFileNameWithoutExtension(entry.FileName);
        var inputDir = Path.GetDirectoryName(entry.FilePath)!;
        var ddxPath = embedTextures ? inputDir : null;
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
            ddxPath);

        return levelTriangles + objectTriangles;
    }

    private static Ps2SceneGltfWriter.TextureProvider? BuildPs2TextureProvider(string? texturePath, bool embedTextures)
    {
        if (!embedTextures || texturePath == null)
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
