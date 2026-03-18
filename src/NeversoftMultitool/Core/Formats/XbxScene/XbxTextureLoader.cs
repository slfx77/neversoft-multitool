using NeversoftMultitool.Core.Formats.Psx;

namespace NeversoftMultitool.Core.Formats.XbxScene;

/// <summary>
///     Loads Xbox/PC TEX textures into a checksum→Ps2Texture cache for glTF embedding.
///     Uses CompanionSearch to discover .tex.xbx companion files.
/// </summary>
public static class XbxTextureLoader
{
    private static readonly string[] TexExtensions = [".tex.xbx", ".tex.wpc", ".tex", ".stex"];
    private static readonly string[] TexDirNames = ["TEX", "Textures"];

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

        // Auto-detect: scan from common root of all input files
        var commonRoot = CompanionSearch.GetCommonRoot(inputFiles);
        if (commonRoot != null)
        {
            var texFiles = CompanionSearch.FindAllByExtension(commonRoot, TexExtensions);
            foreach (var tf in texFiles)
                ParseTexIntoCache(tf, cache, parsedFiles, verbose);
        }

        return cache;
    }

    /// <summary>
    ///     Try to load a companion TEX file for a specific input file.
    /// </summary>
    public static Dictionary<uint, Ps2Texture>? TryLoadCompanionTex(string inputFile, string stem)
    {
        var dir = Path.GetDirectoryName(inputFile);
        if (dir == null) return null;

        var texFile = CompanionSearch.FindCompanion(dir, stem, TexExtensions, TexDirNames);
        if (texFile == null) return null;

        try
        {
            var result = XbxTexFile.Parse(texFile);
            if (!result.Success)
                result = ThawTexFile.Parse(texFile);
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

    private static void ParseTexIntoCache(string texFile,
        Dictionary<uint, Ps2Texture> cache, HashSet<string> parsedFiles, bool verbose)
    {
        if (!parsedFiles.Add(texFile)) return;

        try
        {
            var result = XbxTexFile.Parse(texFile);
            if (!result.Success)
                result = ThawTexFile.Parse(texFile); // Try THAW 0xABADD00D format
            if (!result.Success) return;

            foreach (var tex in result.Textures)
            {
                if (tex.Pixels != null)
                    cache.TryAdd(tex.Checksum, tex);
            }
        }
        catch
        {
            // Skip unparseable files
        }
    }

    private static List<string> GetTexFiles(string path)
    {
        if (File.Exists(path))
            return [path];

        if (Directory.Exists(path))
        {
            return Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)
                .Where(f =>
                {
                    var name = Path.GetFileName(f);
                    return name.EndsWith(".tex.xbx", StringComparison.OrdinalIgnoreCase) ||
                           name.EndsWith(".tex.wpc", StringComparison.OrdinalIgnoreCase) ||
                           name.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) ||
                           name.EndsWith(".stex", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();
        }

        return [];
    }
}
