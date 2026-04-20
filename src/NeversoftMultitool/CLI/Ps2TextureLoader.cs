using NeversoftMultitool.Core;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Texture;
using NeversoftMultitool.Core.Formats.Texture.Ps2;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.SceneTex;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

/// <summary>
///     Shared utilities for loading PS2 TEX files into texture caches.
///     Used by both Ps2SceneCommand and Ps2GeomCommand.
/// </summary>
internal static class Ps2TextureLoader
{
    /// <summary>
    ///     Builds a combined texture cache from an explicit TEX path or by scanning
    ///     from the common root directory of all input files.
    /// </summary>
    public static Dictionary<uint, Ps2Texture> BuildTextureCache(
        List<string> inputFiles, string? texPath, bool verbose)
    {
        var cache = new Dictionary<uint, Ps2Texture>();
        var parsedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // If explicit path provided, parse it
        if (texPath != null)
        {
            var texFiles = GetTexFiles(texPath);
            foreach (var tf in texFiles)
                ParseTexIntoCache(tf, cache, parsedFiles, verbose);
            return cache;
        }

        // Auto-detect: scan from common root of all input files
        var commonRoot = CompanionSearch.GetCommonRoot(inputFiles);
        if (commonRoot != null)
        {
            var texFiles = CompanionSearch.FindAllByExtension(
                commonRoot, [".tex.ps2", ".tex", ".img.ps2", ".stex"]);
            foreach (var tf in texFiles)
                ParseTexIntoCache(tf, cache, parsedFiles, verbose);
        }

        return cache;
    }

    /// <summary>
    ///     Try to load a companion TEX file for a specific input file.
    ///     Searches: same directory → sibling TEX/ → ancestor walk (Textures/, TEX/).
    /// </summary>
    public static Dictionary<uint, Ps2Texture>? TryLoadCompanionTex(string inputFile, string stem)
    {
        var dir = Path.GetDirectoryName(inputFile);
        if (dir == null) return null;

        var texFile = CompanionSearch.FindCompanion(
            dir, stem, [".tex.ps2", ".tex", ".img.ps2", ".stex"], ["TEX", "Textures", "IMG"]);
        if (texFile == null) return null;

        try
        {
            var result = ParseTextureFile(texFile);
            if (!result.Success) return null;

            var cache = new Dictionary<uint, Ps2Texture>();
            foreach (var tex in result.Textures)
            {
                if (tex.Pixels != null)
                    cache.TryAdd(tex.Checksum, tex);
            }

            return cache;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     Builds a TEX0 lookup map used by GEOM-style world geometry where textures
    ///     are referenced by DMA TEX0 state rather than by explicit checksum.
    /// </summary>
    public static Dictionary<(uint Group, uint Tbp, uint Cbp), uint> BuildTex0Mapping(
        List<string> inputFiles, string? texPath, bool verbose)
    {
        var mapping = new Dictionary<(uint Group, uint Tbp, uint Cbp), uint>();
        var parsedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (texPath != null)
        {
            foreach (var tf in GetTexFiles(texPath))
                MergeTex0Mapping(tf, mapping, parsedFiles, verbose);
            return mapping;
        }

        var commonRoot = CompanionSearch.GetCommonRoot(inputFiles);
        if (commonRoot != null)
        {
            var texFiles = CompanionSearch.FindAllByExtension(
                commonRoot, [".tex.ps2", ".tex", ".img.ps2", ".stex"]);
            foreach (var tf in texFiles)
                MergeTex0Mapping(tf, mapping, parsedFiles, verbose);
        }

        return mapping;
    }

    public static void ParseTexIntoCache(string texFile,
        Dictionary<uint, Ps2Texture> cache, HashSet<string> parsedFiles, bool verbose)
    {
        if (!parsedFiles.Add(texFile)) return;

        try
        {
            var result = ParseTextureFile(texFile);
            if (!result.Success) return;

            foreach (var tex in result.Textures)
            {
                if (tex.Pixels != null)
                    cache.TryAdd(tex.Checksum, tex);
            }
        }
        catch (Exception ex)
        {
            if (verbose)
            {
                AnsiConsole.MarkupLine(
                    $"  TEX {Path.GetFileName(texFile)}: [yellow]{ex.Message.EscapeMarkup()}[/]");
            }
        }
    }

    public static void MergeTex0Mapping(string texFile,
        Dictionary<(uint Group, uint Tbp, uint Cbp), uint> mapping,
        HashSet<string> parsedFiles, bool verbose)
    {
        if (!parsedFiles.Add(texFile)) return;

        try
        {
            var fileMapping = Ps2VramAllocator.BuildMapping(texFile);
            foreach (var (key, checksum) in fileMapping)
                mapping.TryAdd(key, checksum);

            if (fileMapping.Count > 0)
                return;

            var thawMap = ThawSceneTexFile.BuildTbpCbpMap(File.ReadAllBytes(texFile));
            foreach (var (key, checksum) in thawMap)
                mapping.TryAdd((0, key.Tbp, key.Cbp), checksum);
        }
        catch (Exception ex)
        {
            if (verbose)
            {
                AnsiConsole.MarkupLine(
                    $"  VRAM {Path.GetFileName(texFile)}: [yellow]{ex.Message.EscapeMarkup()}[/]");
            }
        }
    }

    /// <summary>
    ///     Try to load THAW world-zone TEX files and build texture providers.
    ///     Returns true if zone TEX files were found and providers were created.
    ///     Decodes all textures upfront from the record table, then serves them
    ///     from the cache via checksum or TEX0 TBP/CBP lookup.
    /// </summary>
    public static bool TryBuildZoneTexProviders(
        string? texPath,
        out Ps2SceneGltfWriter.TextureProvider? textureProvider,
        out Ps2GeomGltfWriter.Tex0Resolver? tex0Resolver,
        bool verbose)
    {
        textureProvider = null;
        tex0Resolver = null;

        if (texPath == null) return false;

        var texFiles = GetTexFiles(texPath);
        var textureCache = new Dictionary<uint, Ps2Texture>();
        var checksumByTbpCbp = new Dictionary<(uint Tbp, uint Cbp), uint>();
        var zoneTexCount = 0;

        foreach (var tf in texFiles)
        {
            try
            {
                var data = File.ReadAllBytes(tf);

                // A .pak.ps2 file is an archive containing many .stex/.tex entries. Extracting
                // each entry separately yields far more textures than parsing the whole PAK as
                // a single ThawZoneTex blob (z_bh.pak.ps2 has 9 tex-ish entries but only 3 are
                // findable when the whole PAK is treated as one blob).
                if (tf.EndsWith(".pak.ps2", StringComparison.OrdinalIgnoreCase)
                    && PakArchive.IsPakArchive(tf))
                {
                    foreach (var entry in PakArchive.GetTypedEntries(tf))
                    {
                        if (entry.TypeHash is not (0x2B0A3095u /* .stex */ or 0x8BFA5E8Eu /* .tex */))
                            continue;
                        var off = entry.Entry.Offset;
                        var size = entry.Entry.Size;
                        if (off < 0 || size <= 0 || off + size > data.Length)
                            continue;
                        var entryBytes = new byte[size];
                        Array.Copy(data, off, entryBytes, 0, (int)size);
                        ParseZoneTexBytes(entryBytes, $"{Path.GetFileName(tf)}::{off:X8}",
                            textureCache, checksumByTbpCbp, ref zoneTexCount, verbose);
                    }
                    // Also try the whole-PAK path in case there's top-level zone TEX data.
                    if (ThawZoneTexFile.IsThawZoneTex(data))
                        ParseZoneTexBytes(data, Path.GetFileName(tf),
                            textureCache, checksumByTbpCbp, ref zoneTexCount, verbose);
                }
                else if (ThawZoneTexFile.IsThawZoneTex(data))
                {
                    ParseZoneTexBytes(data, Path.GetFileName(tf),
                        textureCache, checksumByTbpCbp, ref zoneTexCount, verbose);
                }
            }
            catch
            {
                // Skip unreadable files
            }
        }

        if (zoneTexCount == 0) return false;
        if (textureCache.Count == 0) return false;

        if (verbose)
            AnsiConsole.MarkupLine(
                $"Decoded [green]{textureCache.Count}[/] textures from {zoneTexCount} zone TEX file(s)");

        // PNG cache for texture embedding
        var pngCache = new Dictionary<uint, byte[]?>();

        tex0Resolver = (dmaTex0, groupChecksum) =>
        {
            var tbp = (uint)(dmaTex0 & 0x3FFF);
            var cbp = (uint)((dmaTex0 >> 37) & 0x3FFF);
            var checksum = checksumByTbpCbp.TryGetValue((tbp, cbp), out var ck)
                ? ck
                : (tbp << 16) | cbp;
            if (checksum == 0) return 0;

            if (!pngCache.ContainsKey(checksum))
            {
                if (textureCache.TryGetValue(checksum, out var texture) && texture.Pixels != null)
                {
                    pngCache[checksum] = ImageWriter.WritePngToMemory(
                        texture.Width, texture.Height, texture.Pixels);
                }
                else
                {
                    pngCache[checksum] = null;
                }
            }

            return pngCache[checksum] != null ? checksum : 0;
        };

        textureProvider = checksum =>
        {
            pngCache.TryGetValue(checksum, out var png);
            return png;
        };

        return true;
    }

    public static List<string> GetTexFiles(string path)
    {
        if (File.Exists(path))
        {
            // THAW worldzone PAKs carry textures in the main PAK plus several sibling PAKs
            // (z_bh.pak.ps2 + z_bh_*.pak.ps2). Including them broadens the zone-TEX texture pool
            // from ~3 to potentially dozens. Non-zone-TEX sibling PAKs parse as empty and are
            // cheaply filtered downstream via ThawZoneTexFile.IsThawZoneTex.
            if (path.EndsWith(".pak.ps2", StringComparison.OrdinalIgnoreCase))
                return GetSiblingPakFiles(path);
            return [path];
        }

        if (!Directory.Exists(path))
            return [];

        return Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)
            .Where(f =>
            {
                var name = Path.GetFileName(f);
                return name.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)
                       || name.EndsWith(".tex.ps2", StringComparison.OrdinalIgnoreCase)
                       || name.EndsWith(".img.ps2", StringComparison.OrdinalIgnoreCase)
                       || name.EndsWith(".stex", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();
    }

    private static void ParseZoneTexBytes(
        byte[] data,
        string label,
        Dictionary<uint, Ps2Texture> textureCache,
        Dictionary<(uint Tbp, uint Cbp), uint> checksumByTbpCbp,
        ref int zoneTexCount,
        bool verbose)
    {
        if (!ThawZoneTexFile.IsThawZoneTex(data))
            return;

        zoneTexCount++;
        var textures = ThawZoneTexFile.DecodeAllFromFile(data);
        var entries = ThawZoneTexFile.ParseHeaderEntries(data);

        foreach (var texture in textures)
            textureCache.TryAdd(texture.Checksum, texture);

        foreach (var entry in entries)
        {
            var tbp = (uint)(entry.Tex0 & 0x3FFF);
            var cbp = (uint)((entry.Tex0 >> 37) & 0x3FFF);
            checksumByTbpCbp.TryAdd((tbp, cbp), entry.Checksum);
        }

        if (verbose)
        {
            AnsiConsole.MarkupLine(
                $"Detected zone TEX: [green]{label}[/] ({entries.Count} records, {textures.Count} textures)");
        }
    }

    private static List<string> GetSiblingPakFiles(string pakPath)
    {
        var dir = Path.GetDirectoryName(pakPath);
        if (dir == null || !Directory.Exists(dir))
            return [pakPath];

        var stem = GetZoneStem(Path.GetFileName(pakPath));
        if (stem == null)
            return [pakPath];

        // Dedupe by canonical full path since the user-provided pakPath and the paths returned
        // by EnumerateFiles may differ in slash direction and casing.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        void Add(string p)
        {
            var canonical = Path.GetFullPath(p);
            if (seen.Add(canonical))
                result.Add(p);
        }

        Add(pakPath);
        foreach (var candidate in Directory.EnumerateFiles(dir, $"{stem}*.pak.ps2"))
            Add(candidate);
        return result;
    }

    private static string? GetZoneStem(string fileName)
    {
        // Match THAW worldzone naming: stem is the leading "z_<code>" or "z_<code>_<suffix>" token
        // before the first '.' or first underscore beyond the zone code. For "z_bh.pak.ps2" the
        // stem is "z_bh"; for "z_bhped.pak.ps2" it's "z_bhped"; for "z_bh_net.pak.ps2" it's "z_bh"
        // so sibling scans group all z_bh* PAKs together.
        var dot = fileName.IndexOf('.');
        if (dot <= 0) return null;
        var stem = fileName[..dot];
        // If stem contains an underscore past the "z_" prefix, truncate to that prefix group.
        if (stem.StartsWith("z_", StringComparison.OrdinalIgnoreCase))
        {
            var underscore = stem.IndexOf('_', 2);
            if (underscore > 0)
                stem = stem[..underscore];
        }
        return stem.Length > 0 ? stem : null;
    }

    private static Ps2TexResult ParseTextureFile(string texFile)
    {
        var result = Ps2TexFile.Parse(texFile);
        if (!result.Success)
            result = ThawSceneTexFile.Parse(texFile);
        if (!result.Success)
            result = ThawSceneTexFile.ParsePermissive(texFile);
        return result;
    }
}
