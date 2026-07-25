using System.Text.RegularExpressions;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Mesh.Lit;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Texture;
using NeversoftMultitool.Core.Formats.Texture.Ngc;
using NeversoftMultitool.Core.Formats.Texture.Ps2;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.SceneTex;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;
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

    /// <summary>
    ///     Classifies a PSX file as a placeable level and resolves its trigger
    ///     stem and model-bank companion, across the engine lineage's naming
    ///     schemes:
    ///     <list type="bullet">
    ///         <item>
    ///             Spider-Man <c>*_g.psx</c> geometry → sibling <c>*_o.psx</c>
    ///             bank, resolved unconditionally (the bank is optional — the
    ///             POWERUP/items layer still runs without it).
    ///         </item>
    ///         <item>
    ///             THPS1/THPS2 <c>*.psx</c> level geometry carries no marker
    ///             suffix, so it qualifies only when both a sibling <c>*_o.psx</c> bank
    ///             AND a <c>*_t.trg</c> trigger file exist. That sibling requirement
    ///             self-excludes the <c>_o</c>/<c>_l</c>/<c>_2</c> companions (they have
    ///             no <c>_o</c> of their own) and standalone character models (which
    ///             have neither companion), so no suffix blocklist is needed.
    ///         </item>
    ///     </list>
    ///     The bank object table is the same placed-layer convention in both
    ///     families (authored world positions, div 2.25), and both ship v2.x TRGs
    ///     whose PLATFORM nodes reference bank models — verified coincident with
    ///     the bank instances (THPS1 24/30, THPS2 12/17 at δ≈0).
    /// </summary>
    internal static bool TryResolvePsxLevelCompanions(
        AssetSource source,
        string fileName,
        out PsxLevelCompanions companions)
    {
        companions = default;
        if (!Path.GetExtension(fileName).Equals(".psx", StringComparison.OrdinalIgnoreCase))
            return false;

        var stem = Path.GetFileNameWithoutExtension(fileName);

        if (stem.Length > 2 && stem.EndsWith("_g", StringComparison.OrdinalIgnoreCase))
        {
            var levelStem = stem[..^2];
            companions = new PsxLevelCompanions(levelStem, levelStem + "_o.psx", true);
            return true;
        }

        if (source.CompanionExists(stem + "_o.psx")
            && source.CompanionExists(stem + "_t.trg"))
        {
            companions = new PsxLevelCompanions(stem, stem + "_o.psx", true);
            return true;
        }

        return TryResolveApocalypseLevel(source, stem, out companions);
    }

    /// <summary>
    ///     Apocalypse levels are split into <c>&lt;base&gt;_&lt;chunk&gt;</c>
    ///     geometry pieces (chunk = a number with an optional letter, e.g.
    ///     <c>city_8a</c>) plus an optional bare <c>&lt;base&gt;.psx</c>, sharing a
    ///     model bank named <c>&lt;base&gt;_obj.psx</c> or (older, no separator)
    ///     <c>&lt;base&gt;obj.psx</c> and a <c>&lt;base&gt;_t.trg</c> trigger. The
    ///     bank is attached to exactly ONE primary per level so a batch convert
    ///     does not place the shared bank once per chunk: the bare
    ///     <c>&lt;base&gt;.psx</c> if present, otherwise the first chunk
    ///     (<c>_1</c>/<c>_1a</c>). The PLATFORM/POWERUP layers use the same
    ///     div-2.25 node scale as the other games (see
    ///     <see cref="PsxLevelCompanions" />), verified in-bounds against the
    ///     Apocalypse binary's pickup constructor.
    /// </summary>
    private static bool TryResolveApocalypseLevel(
        AssetSource source,
        string stem,
        out PsxLevelCompanions companions)
    {
        companions = default;

        // Bare primary: the file's own stem carries the bank + trigger.
        if (TryGetApocalypseBankName(source, stem, out var bareBank)
            && source.CompanionExists(stem + "_t.trg"))
        {
            companions = new PsxLevelCompanions(stem, bareBank, true);
            return true;
        }

        // Chunk primary: <base>_1 / <base>_1a, only when no bare <base>.psx owns
        // the attach (which would double-place the shared bank).
        var separator = stem.LastIndexOf('_');
        if (separator <= 0 || separator == stem.Length - 1)
            return false;

        var chunk = stem[(separator + 1)..];
        if (!IsApocalypseChunkSuffix(chunk)
            || !(chunk is "1" or "1a"))
        {
            return false;
        }

        var baseStem = stem[..separator];
        if (source.CompanionExists(baseStem + ".psx")
            || !TryGetApocalypseBankName(source, baseStem, out var bank)
            || !source.CompanionExists(baseStem + "_t.trg"))
        {
            return false;
        }

        companions = new PsxLevelCompanions(baseStem, bank, true);
        return true;
    }

    private static bool TryGetApocalypseBankName(AssetSource source, string baseStem, out string bankName)
    {
        if (source.CompanionExists(baseStem + "_obj.psx"))
        {
            bankName = baseStem + "_obj.psx";
            return true;
        }

        if (source.CompanionExists(baseStem + "obj.psx"))
        {
            bankName = baseStem + "obj.psx";
            return true;
        }

        bankName = string.Empty;
        return false;
    }

    /// <summary>A chunk suffix is a run of digits with at most one trailing letter.</summary>
    private static bool IsApocalypseChunkSuffix(string chunk)
    {
        var digits = 0;
        while (digits < chunk.Length && char.IsAsciiDigit(chunk[digits]))
            digits++;

        if (digits == 0)
            return false;

        var rest = chunk.Length - digits;
        return rest == 0 || (rest == 1 && char.IsAsciiLetter(chunk[digits]));
    }

    internal static bool HasSupportedLevelObjectCompanion(
        AssetSource source,
        string fileName)
    {
        return TryResolvePsxLevelCompanions(source, fileName, out var companions)
               && source.CompanionExists(companions.BankCompanionName);
    }

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
        if (texBytes == null)
        {
            // THPS3 LOD variants (*_LOD00.skn etc., 237 files) ship no same-stem
            // .tex — their textures live in the base model's dictionary in the
            // same directory (verified 237/237 name-resolvable there).
            var baseStem = Regex.Replace(
                stem, @"_LOD\d+$", string.Empty,
                RegexOptions.IgnoreCase);
            if (!string.Equals(baseStem, stem, StringComparison.Ordinal))
                texBytes = ReadTextureCompanion(source, baseStem, RwTexExtensions, RwTexSubdirs, explicitTexturePath);
        }

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

        // THPS1's selected skateboard equipment is stored separately from the
        // character PSX. The 16x16 w_*.bmp wheel and 32x16 wt*.bmp truck art
        // carry the authored magenta cutout that is absent from several embedded
        // character palettes. Only board-subtree material hashes may consume the
        // masks, so unrelated textures of the same dimensions remain untouched.
        var boardEquipmentHashes = FindPsxBoardEquipmentTextureHashes(psxData);
        var equipmentMasks = new List<byte[]>(2);
        if (boardEquipmentHashes.Count > 0)
        {
            var wheelMask = source.TryReadCompanion("w_blue01.bmp");
            if (wheelMask != null)
                equipmentMasks.Add(wheelMask);
            var truckMask = source.TryReadCompanion("wtblue01.bmp");
            if (truckMask != null)
                equipmentMasks.Add(truckMask);
        }

        return hash =>
        {
            var result = PsxLibrary.ExtractTextureByHash(
                psxData,
                hash,
                meshLabel,
                preserveRuntimeSemiTransparency: true);
            for (var i = 0; result == null && i < libraries.Count; i++)
            {
                result = PsxLibrary.ExtractTextureByHash(
                    libraries[i].Bytes,
                    hash,
                    libraries[i].Label,
                    preserveRuntimeSemiTransparency: true);
            }

            if (result == null)
                return null;
            var (rgba, width, height) = result.Value;
            var pngBytes = ImageWriter.WritePngToMemory(width, height, rgba);
            if (!boardEquipmentHashes.Contains(hash))
                return pngBytes;

            foreach (var mask in equipmentMasks)
            {
                var masked = MeshTextureHelper.ApplyExternalMagentaMask(pngBytes, mask);
                if (masked.Applied)
                    return masked.Bytes;
            }

            return pngBytes;
        };
    }

    private static HashSet<uint> FindPsxBoardEquipmentTextureHashes(byte[] psxData)
    {
        try
        {
            var psxFile = PsxMeshFile.Parse(psxData);
            if (psxFile == null || psxFile.Objects.Count == 0)
                return [];

            var equipmentObjects = new HashSet<int>();
            for (var meshIndex = 0; meshIndex < psxFile.Meshes.Count; meshIndex++)
            {
                var meshName = PsxGeometryHelpers.ResolvePsxMeshName(psxFile, meshIndex);
                if (!meshName.Equals("board", StringComparison.OrdinalIgnoreCase) &&
                    !meshName.EndsWith("_board", StringComparison.OrdinalIgnoreCase))
                    continue;

                for (var objectIndex = 0; objectIndex < psxFile.Objects.Count; objectIndex++)
                    if (psxFile.Objects[objectIndex].MeshIndex == meshIndex)
                        equipmentObjects.Add(objectIndex);
            }

            if (equipmentObjects.Count == 0)
                return [];

            // Wheel objects are children of the board object. Walk to a fixed
            // point rather than assuming the bone/mesh order (Hawk and Muska
            // use different orders, and Muska combines both wheels in one mesh).
            var added = true;
            while (added)
            {
                added = false;
                for (var objectIndex = 0; objectIndex < psxFile.Objects.Count; objectIndex++)
                {
                    var parent = psxFile.Objects[objectIndex].ParentIndex;
                    if (parent < 0 || !equipmentObjects.Contains(parent))
                        continue;
                    added |= equipmentObjects.Add(objectIndex);
                }
            }

            var hashes = new HashSet<uint>();
            foreach (var objectIndex in equipmentObjects)
            {
                var meshIndex = psxFile.Objects[objectIndex].MeshIndex;
                if (meshIndex >= psxFile.Meshes.Count)
                    continue;
                foreach (var face in psxFile.Meshes[meshIndex].Faces)
                    if (face.IsTextured && face.TextureHash != 0)
                        hashes.Add(face.TextureHash);
            }

            return hashes;
        }
        catch
        {
            return [];
        }
    }

    internal static MeshChecksumTextureResolver? BuildPs2TextureProvider(byte[]? textureBytes)
    {
        if (textureBytes == null) return null;

        var texResult = Ps2TexFile.Parse(textureBytes);
        if (!texResult.Success)
            texResult = ThawSceneTexFile.Parse(textureBytes);
        if (!texResult.Success && ThawZoneTexFile.IsThawZoneTex(textureBytes))
            texResult = new Ps2TexResult(ThawZoneTexFile.DecodeAllFromFile(textureBytes));
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
        string? explicitPath = null,
        bool searchBuildTree = false)
    {
        var path = ResolveExplicitPath(explicitPath, stem, extensions, subdirs);
        if (path != null)
            return File.ReadAllBytes(path);

        var bytes = source.TryReadCompanion(stem, extensions, subdirs);
        if (bytes == null && source is ArchiveAssetSource archiveSource)
            bytes = TryReadNearestPrecedingArchiveCompanion(archiveSource, extensions);
        if (bytes == null && searchBuildTree)
            bytes = BuildTreeCompanionLocator.TryReadTextureCompanion(source, stem, extensions);
        return bytes;
    }

    /// <summary>
    ///     THAW packages with stripped filenames store an anonymous texture entry
    ///     immediately before its anonymous MDL/SKIN entry (for example
    ///     000061B0.stex then 0000EEC0.mdl). Same-stem lookup cannot work for
    ///     those generated offset names, so fall back to the closest preceding
    ///     texture in the selected entry's package directory.
    /// </summary>
    private static byte[]? TryReadNearestPrecedingArchiveCompanion(
        ArchiveAssetSource source,
        IReadOnlyList<string> extensions)
    {
        if (source.Backend.Type != ArchiveAssetType.Pak || !HasOffsetGeneratedName(source.Entry.Name))
            return null;

        var entry = source.Backend.Entries
            .Where(candidate => candidate.Offset < source.Entry.Offset)
            .Where(candidate => string.Equals(
                candidate.Directory, source.Entry.Directory, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => extensions.Any(extension =>
                candidate.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(static candidate => candidate.Offset)
            .FirstOrDefault();

        return entry == null ? null : source.Backend.ReadEntryBytes(entry);
    }

    private static bool HasOffsetGeneratedName(string entryName)
    {
        var stem = Path.GetFileNameWithoutExtension(entryName);
        return stem.Length == 8 && stem.All(static character =>
            character is >= '0' and <= '9'
                or >= 'A' and <= 'F'
                or >= 'a' and <= 'f');
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

    /// <summary>
    ///     A PSX level file's companion naming: the trigger/items lookup stem, the
    ///     model-bank filename, and whether the sibling TRG's PLATFORM/MANIPOB
    ///     model overlay should be applied. Enabled for every supported game — all
    ///     place their TRG nodes at div 2.25 (Spider-Man and THPS PLATFORM nodes
    ///     are coincident with the bank; Apocalypse nodes are re-instances that
    ///     stay in-bounds, verified against its pickup constructor's node scale).
    ///     The per-game flag is retained as the knob to gate a build whose overlay
    ///     placement later proves unfaithful.
    /// </summary>
    internal readonly record struct PsxLevelCompanions(
        string LevelStem,
        string BankCompanionName,
        bool ApplyTriggerOverlay);
}
