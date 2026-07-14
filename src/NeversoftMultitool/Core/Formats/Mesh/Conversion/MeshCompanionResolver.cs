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
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;
using NeversoftMultitool.Core.Formats.Texture;
using NeversoftMultitool.Core.Formats.Texture.Ngc;
using NeversoftMultitool.Core.Formats.Texture.Ps2;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.SceneTex;
using NeversoftMultitool.Core.Formats.Texture.Psx;
using NeversoftMultitool.Core.Formats.Texture.RenderWare;
using NeversoftMultitool.Core.Formats.Texture.XbxScene;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Companion-file discovery for mesh conversion: DDX/LIT siblings, texture
///     dictionaries per platform, and PS2 skeleton resolution.
/// </summary>
internal static class MeshCompanionResolver
{
    private static readonly string[] XbxTexExtensions = [".tex.xbx", ".tex.wpc"];
    private static readonly string[] NgcTexExtensions = [".tex.ngc"];
    private static readonly string[] XbxTexSubdirs = ["TEX", "Textures"];
    private static readonly string[] RwTexExtensions = [".tex"];
    private static readonly string[] RwTexSubdirs = ["TEX", "Textures"];

    internal static Dictionary<string, byte[]>? LoadDdxCompanion(
        AssetSource source,
        string stem,
        string? explicitPath = null)
    {
        var ddxPath = ResolveExplicitPath(explicitPath, stem, [".ddx"], []);
        if (ddxPath != null)
            return DdxArchive.ReadAllEntries(ddxPath);

        var ddxBytes = source.TryReadCompanion(stem + ".ddx");
        return ddxBytes != null ? DdxArchive.ReadAllEntries(ddxBytes) : null;
    }

    internal static List<LitLight>? LoadLitCompanion(AssetSource source, string stem)
    {
        var litBytes = source.TryReadCompanion(stem + ".lit");
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

    internal static MeshNamedTextureResolver? BuildRwTxdTextureProvider(
        AssetSource source,
        string fileName,
        string? explicitTexturePath = null)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var texBytes = ReadTextureCompanion(source, stem, RwTexExtensions, RwTexSubdirs, explicitTexturePath);
        if (texBytes == null) return null;
        var txdResult = RwTxdFile.Parse(texBytes);
        if (!txdResult.Success) return null;

        var lookup = new Dictionary<string, Ps2Texture>(StringComparer.OrdinalIgnoreCase);
        foreach (var tex in txdResult.Textures)
            if (tex.Pixels != null && tex.Name != null)
                lookup.TryAdd(tex.Name, tex);

        return textureName =>
        {
            if (!lookup.TryGetValue(textureName, out var tex))
            {
                var extIdx = textureName.LastIndexOf('.');
                if (extIdx <= 0 || !lookup.TryGetValue(textureName[..extIdx], out tex))
                    return null;
            }

            return ImageWriter.WritePngToMemory(tex.Width, tex.Height, tex.Pixels!);
        };
    }

    /// <summary>
    ///     THAW GC material passes reference textures by ORDER in the companion
    ///     .tex.ngc dictionary; the parser stores index+1 in TextureChecksum.
    /// </summary>
    internal static MeshChecksumTextureResolver? BuildNgcSceneTextureProvider(
        AssetSource source,
        string stem,
        string? explicitTexturePath = null)
    {
        var texBytes = ReadTextureCompanion(source, stem, NgcTexExtensions, XbxTexSubdirs, explicitTexturePath);
        if (texBytes == null) return null;

        var texResult = NgcTexFile.Parse(texBytes);
        if (!texResult.Success) return null;

        var ordered = texResult.Textures;
        return value =>
        {
            var index = (int)value - 1;
            if (index < 0 || index >= ordered.Count || ordered[index].Pixels == null)
                return null;
            var tex = ordered[index];
            return ImageWriter.WritePngToMemory(tex.Width, tex.Height, tex.Pixels!);
        };
    }

    internal static MeshChecksumTextureResolver? BuildXbxSceneTextureProvider(
        AssetSource source,
        string stem,
        string? explicitTexturePath = null)
    {
        var texBytes = ReadTextureCompanion(source, stem, XbxTexExtensions, XbxTexSubdirs, explicitTexturePath);
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

    internal static MeshChecksumTextureResolver BuildPsxTextureProvider(
        AssetSource source,
        string fileName,
        byte[] psxData)
    {
        var meshLabel = fileName;
        var stem = Path.GetFileNameWithoutExtension(fileName);

        // Sibling texture libraries per the same candidate rules as
        // PsxTextureProviderFactory (retail *_g/_l pairs, proto {stem}_l /
        // base _l / shared skatelib+sub_lib).
        var libraries = new List<(byte[] Bytes, string Label)>();
        foreach (var candidate in PsxTextureProviderFactory.GetCompanionLibraryStems(stem))
        {
            var libraryName = candidate + ".psx";
            var bytes = source.TryReadCompanion(libraryName);
            if (bytes != null)
                libraries.Add((bytes, libraryName));
        }

        return hash =>
        {
            var result = PsxLibrary.ExtractTextureByHash(psxData, hash, meshLabel);
            for (var i = 0; result == null && i < libraries.Count; i++)
                result = PsxLibrary.ExtractTextureByHash(libraries[i].Bytes, hash, libraries[i].Label);
            if (result == null)
                return null;
            var (rgba, width, height) = result.Value;
            return ImageWriter.WritePngToMemory(width, height, rgba);
        };
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

    internal static Ps2Skeleton? TryLoadPs2Skeleton(
        AssetSource source,
        string stem,
        Ps2SceneSubFormat subFormat,
        string? explicitSkeletonPath = null)
    {
        var explicitPath = ResolveExplicitPath(
            explicitSkeletonPath,
            stem,
            [".ske.ps2", ".ske.ngc", ".ske"],
            ["SKE", "Skeletons"]);
        if (explicitPath != null)
        {
            try
            {
                return explicitPath.EndsWith(".ske.ps2", StringComparison.OrdinalIgnoreCase)
                    ? Ps2SkeletonFile.Parse(explicitPath)
                    : SkeletonFile.Parse(explicitPath);
            }
            catch
            {
                /* fall through to automatic discovery */
            }
        }

        var ps2Bytes = source.TryReadCompanion(stem + ".ske.ps2");
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
            var skeBytes = source.TryReadCompanion(stem + extension);
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

        if (subFormat == Ps2SceneSubFormat.ThawSkin && source.FileSystemPath != null)
        {
            var skeletonPath = ThawSkeletonDiscovery.FindSkeletonPath(
                source.FileSystemPath, stem, true);
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

        if (subFormat == Ps2SceneSubFormat.ThawSkin && source is ArchiveAssetSource archiveSource)
        {
            var archiveResult = ThawSkeletonDiscovery.FindInArchive(
                archiveSource.Backend.Entries, archiveSource.Backend, stem, true);
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

    internal static byte[]? ReadTextureCompanion(
        AssetSource source,
        string stem,
        string[] extensions,
        string[] subdirs,
        string? explicitPath = null)
    {
        var path = ResolveExplicitPath(explicitPath, stem, extensions, subdirs);
        if (path != null)
            return File.ReadAllBytes(path);

        return source.TryReadCompanion(stem, extensions, subdirs);
    }

    internal static string? ResolveCompanionPath(
        AssetSource source,
        string stem,
        string extension,
        string? explicitPath)
    {
        var path = ResolveExplicitPath(explicitPath, stem, [extension], []);
        return path ?? source.TryResolveCompanionPath(stem + extension);
    }

    internal static string? ResolveExplicitPath(
        string? explicitPath,
        string stem,
        string[] extensions,
        string[] subdirs)
    {
        if (string.IsNullOrWhiteSpace(explicitPath))
            return null;

        if (File.Exists(explicitPath))
            return explicitPath;

        if (!Directory.Exists(explicitPath))
            return null;

        return CompanionSearch.FindCompanion(explicitPath, stem, extensions, subdirs);
    }
}
