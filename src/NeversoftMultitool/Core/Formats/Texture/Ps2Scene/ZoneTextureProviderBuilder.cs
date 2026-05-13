using NeversoftMultitool.Core.Formats.Mesh;

namespace NeversoftMultitool.Core.Formats.Texture.Ps2Scene;

/// <summary>
///     Decodes THAW world-zone TEX files (embedded in .pak.ps2 archives and their
///     sibling PAKs) and builds texture providers / TEX0 resolvers used by the
///     worldzone mesh pipeline. UI-agnostic — lives in Core so both CLI and GUI
///     callers can use it.
/// </summary>
public static class ZoneTextureProviderBuilder
{
    /// <summary>
    ///     Collects .tex / .tex.ps2 / .img.ps2 / .stex files reachable from <paramref name="path" />.
    ///     When <paramref name="path" /> is a .pak.ps2 file, also includes sibling worldzone PAKs
    ///     (z_bh.pak.ps2 + z_bh_*.pak.ps2) so their embedded textures can be pooled together.
    /// </summary>
    public static List<string> GetTexFiles(string path)
    {
        if (File.Exists(path))
        {
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

    /// <summary>
    ///     Try to load THAW world-zone TEX files and build texture providers.
    ///     Returns true if zone TEX files were found and providers were created.
    ///     Decodes all textures upfront, then serves them from the cache via
    ///     checksum or TEX0 TBP/CBP lookup.
    /// </summary>
    public static bool TryBuild(
        string? texPath,
        out MeshChecksumTextureResolver? textureProvider,
        out Ps2Tex0ChecksumResolver? tex0Resolver,
        Action<string>? log = null)
    {
        textureProvider = null;
        tex0Resolver = null;

        if (!ZoneTextureCatalog.TryBuild(texPath, out var catalog, log) || catalog == null)
            return false;

        textureProvider = catalog.CreateTextureResolver();
        tex0Resolver = catalog.CreateTex0ChecksumResolver(texPath);
        return true;
    }

    /// <summary>
    ///     Keep the TEX0 bits that identify a texture instance: TBP (base pointer
    ///     0-13), PSM (pixel format 20-25), TW (width exp 26-29), TH (height exp
    ///     30-33), CBP (CLUT pointer 37-50), CPSM (CLUT format 51-54). Strip the
    ///     rendering-state bits so two TEX0 writes that reference the same texture
    ///     under different render state collapse to one key.
    /// </summary>
    internal static ulong MakeTex0IdentityKey(ulong tex0)
    {
        var tbp = tex0 & 0x3FFFUL;
        var tbw = (tex0 >> 14) & 0x3FUL;
        var psm = (tex0 >> 20) & 0x3FUL;
        var tw = (tex0 >> 26) & 0xFUL;
        var th = (tex0 >> 30) & 0xFUL;
        var cbp = (tex0 >> 37) & 0x3FFFUL;
        var cpsm = (tex0 >> 51) & 0xFUL;
        var csm = (tex0 >> 55) & 0x1UL;
        var csa = (tex0 >> 56) & 0x1FUL;
        return tbp | (tbw << 14) | (psm << 20) | (tw << 26) | (th << 30) |
               (cbp << 34) | (cpsm << 48) | (csm << 52) | (csa << 53);
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
        {
            var candidateStem = GetPakStem(Path.GetFileName(candidate));
            if (candidateStem == null)
                continue;
            if (string.Equals(candidateStem, stem, StringComparison.OrdinalIgnoreCase)
                || candidateStem.StartsWith(stem + "_", StringComparison.OrdinalIgnoreCase))
                Add(candidate);
        }

        return result;
    }

    private static string? GetZoneStem(string fileName)
    {
        // Match THAW worldzone naming: stem is the leading "z_<code>" token before the first '.'.
        // For "z_bh_net.pak.ps2" the stem is "z_bh" so sibling scans group all z_bh* PAKs together.
        var dot = fileName.IndexOf('.');
        if (dot <= 0) return null;
        var stem = fileName[..dot];
        if (stem.StartsWith("z_", StringComparison.OrdinalIgnoreCase))
        {
            var underscore = stem.IndexOf('_', 2);
            if (underscore > 0)
                stem = stem[..underscore];
        }

        return stem.Length > 0 ? stem : null;
    }

    private static string? GetPakStem(string fileName)
    {
        const string PakPs2 = ".pak.ps2";
        if (fileName.EndsWith(PakPs2, StringComparison.OrdinalIgnoreCase))
            return fileName[..^PakPs2.Length];

        var dot = fileName.IndexOf('.');
        return dot > 0 ? fileName[..dot] : null;
    }
}
