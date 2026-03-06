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
                commonRoot, [".tex.ps2", ".tex", ".img.ps2"]);
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
            dir, stem, [".tex.ps2", ".tex", ".img.ps2"], ["TEX", "Textures", "IMG"]);
        if (texFile == null) return null;

        try
        {
            var result = Ps2TexFile.Parse(texFile);
            if (!result.Success)
                result = ThawSceneTexFile.Parse(texFile);
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

    public static void ParseTexIntoCache(string texFile,
        Dictionary<uint, Ps2Texture> cache, HashSet<string> parsedFiles, bool verbose)
    {
        if (!parsedFiles.Add(texFile)) return;

        try
        {
            var result = Ps2TexFile.Parse(texFile);
            if (!result.Success)
                result = ThawSceneTexFile.Parse(texFile);
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
                       || name.EndsWith(".img.ps2", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();
    }
}
