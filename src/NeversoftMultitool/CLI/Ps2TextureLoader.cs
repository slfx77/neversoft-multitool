using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Ps2Scene;
using NeversoftMultitool.Core.Formats.Psx;
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
    ///     Try to load a THAW world-zone TEX file and build VRAM-backed providers.
    ///     Returns true if the file is a zone TEX and providers were created.
    ///     The tex0Resolver lazy-decodes textures from the VRAM map using the TEX0 register
    ///     values from the MDL, and the textureProvider serves the cached results.
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
        var uploads = new List<ThawZoneTexFile.VramUpload>();
        var zoneSources = new List<(ReadOnlyMemory<byte> FileData, List<ThawZoneTexFile.VramUpload> Uploads,
            List<ThawZoneTexFile.ZoneTexHeaderEntry> Headers)>();
        var zoneTexData = new List<ReadOnlyMemory<byte>>();
        var zoneTexCount = 0;

        foreach (var tf in texFiles)
        {
            try
            {
                var data = File.ReadAllBytes(tf);
                if (!ThawZoneTexFile.IsThawZoneTex(data)) continue;

                zoneTexCount++;
                zoneTexData.Add(data);
                var fileUploads = ThawZoneTexFile.ParseVramUploads(data);
                var headerEntries = ThawZoneTexFile.ParseHeaderEntries(data);
                uploads.AddRange(fileUploads);
                zoneSources.Add((data, fileUploads, headerEntries));

                if (verbose)
                {
                    AnsiConsole.MarkupLine(
                        $"Detected zone TEX: [green]{Path.GetFileName(tf)}[/] ({fileUploads.Count} uploads, {headerEntries.Count} header entries)");
                }
            }
            catch
            {
                // Skip unreadable files
            }
        }

        if (zoneTexCount == 0) return false;
        if (uploads.Count == 0) return false;

        if (verbose)
            AnsiConsole.MarkupLine(
                $"Merged [green]{uploads.Count}[/] VRAM uploads from {zoneTexCount} zone TEX file(s)");

        var checksumMap = ThawZoneTexFile.BuildChecksumMapFromHeaders(zoneTexData);
        var headerSourceEntryMapByTex0 = ThawZoneTexFile.BuildHeaderSourceEntryMapByTex0FromHeaderLists(
            zoneSources.Select(static source => (IReadOnlyList<ThawZoneTexFile.ZoneTexHeaderEntry>)source.Headers));
        var headerEntryMap = ThawZoneTexFile.BuildHeaderEntryMapFromHeaderLists(
            zoneSources.Select(static source => (IReadOnlyList<ThawZoneTexFile.ZoneTexHeaderEntry>)source.Headers));
        var sourceIndexMap = ThawZoneTexFile.BuildSourceIndexMapFromHeaderLists(
            zoneSources.Select(static source => (IReadOnlyList<ThawZoneTexFile.ZoneTexHeaderEntry>)source.Headers));

        // Lazy decode cache: synthetic checksum → PNG bytes
        var pngCache = new Dictionary<uint, byte[]?>();

        tex0Resolver = (dmaTex0, groupChecksum) =>
        {
            var tbp = (uint)(dmaTex0 & 0x3FFF);
            var cbp = (uint)((dmaTex0 >> 37) & 0x3FFF);
            var hasResolvedHeader = headerSourceEntryMapByTex0.TryGetValue(dmaTex0, out var resolvedHeader);
            var checksum = hasResolvedHeader
                ? resolvedHeader.Entry.Checksum
                : checksumMap.TryGetValue((tbp, cbp), out var headerChecksum)
                    ? headerChecksum
                    : (tbp << 16) | cbp;
            if (checksum == 0) return 0;

            if (!pngCache.ContainsKey(checksum))
            {
                var hasSource = sourceIndexMap.TryGetValue((tbp, cbp), out var sourceIndex);
                var sourceUploads = hasResolvedHeader
                    ? zoneSources[resolvedHeader.SourceIndex].Uploads
                    : hasSource
                        ? zoneSources[sourceIndex].Uploads
                        : uploads;
                var textures = hasResolvedHeader
                    ? ThawZoneTexFile.DecodeFromHeaderEntries(
                        zoneSources[resolvedHeader.SourceIndex].FileData.Span, sourceUploads, [resolvedHeader.Entry])
                    : hasSource && headerEntryMap.TryGetValue((tbp, cbp), out var headerEntry)
                        ? ThawZoneTexFile.DecodeFromHeaderEntries(
                            zoneSources[sourceIndex].FileData.Span, sourceUploads, [headerEntry])
                        : ThawZoneTexFile.DecodeFromTex0Values(sourceUploads, [dmaTex0], checksumMap);
                var texture = textures.FirstOrDefault();
                if (texture?.Pixels != null)
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
            return [path];

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
