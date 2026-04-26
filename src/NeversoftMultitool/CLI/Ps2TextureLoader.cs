using NeversoftMultitool.Core;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Texture;
using NeversoftMultitool.Core.Formats.Texture.Ps2;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.SceneTex;
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

        if (texPath != null)
        {
            var texFiles = GetTexFiles(texPath);
            foreach (var tf in texFiles)
                ParseTexIntoCache(tf, cache, parsedFiles, verbose);
            return cache;
        }

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
    ///     Thin Spectre-logging wrapper over
    ///     <see cref="ZoneTextureProviderBuilder.TryBuild"/>.
    /// </summary>
    public static bool TryBuildZoneTexProviders(
        string? texPath,
        out Ps2SceneGltfWriter.TextureProvider? textureProvider,
        out Ps2GeomGltfWriter.Tex0Resolver? tex0Resolver,
        bool verbose)
    {
        Action<string>? log = verbose
            ? line => AnsiConsole.MarkupLine(Markup.Escape(line))
            : null;
        return ZoneTextureProviderBuilder.TryBuild(texPath, out textureProvider, out tex0Resolver, log);
    }

    public static List<string> GetTexFiles(string path) =>
        ZoneTextureProviderBuilder.GetTexFiles(path);

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
