using NeversoftMultitool.Core.Formats.Archives;
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

    /// <summary>
    ///     Archive-nested equivalent of the sibling-PAK gather: for a worldzone PAK
    ///     entry inside a parent archive (e.g. z_bh.pak.ps2 inside DATAP.WAD),
    ///     collects the entry itself plus every same-directory sibling whose name
    ///     shares the zone stem (z_bh.pak.ps2 + z_bh_*.pak.ps2), reading each
    ///     entry's bytes from the backend. The main entry is flagged for
    ///     source-hint resolution.
    /// </summary>
    public static List<ZoneTextureCatalog.ZoneTexSource> GetTexByteSources(
        ArchiveAssetBackend backend,
        ArchiveEntry mainEntry)
    {
        var sources = new List<ZoneTextureCatalog.ZoneTexSource>();

        void Add(ArchiveEntry entry, bool isMain)
        {
            try
            {
                sources.Add(new ZoneTextureCatalog.ZoneTexSource(
                    entry.FullName,
                    backend.ReadEntryBytes(entry),
                    isMain));
            }
            catch
            {
                // Skip unreadable entries.
            }
        }

        Add(mainEntry, true);

        var stem = GetZoneStem(mainEntry.Name);
        if (stem == null)
            return sources;

        foreach (var candidate in backend.Entries)
        {
            if (ReferenceEquals(candidate, mainEntry))
                continue;
            if (!string.Equals(candidate.Directory, mainEntry.Directory, StringComparison.OrdinalIgnoreCase))
                continue;
            if (IsSiblingPakFileName(candidate.Name, stem))
                Add(candidate, false);
        }

        // Mission worldzone paks (missions/worldzones/m_z<code>*) stream on top
        // of their base zone in-game and depend on its texture dictionaries —
        // m_zbhgaps4_success's own level .tex is a 0-record stub. Pool the base
        // zone's paks LAST so mission-local dictionaries keep first-wins priority.
        foreach (var zonePak in FindMissionBaseZoneEntries(backend, mainEntry))
            Add(zonePak, false);

        return sources;
    }

    private static List<ArchiveEntry> FindMissionBaseZoneEntries(
        ArchiveAssetBackend backend,
        ArchiveEntry mainEntry)
    {
        if (!IsMissionPakFileName(mainEntry.Name))
            return [];

        const string ZonesRoot = "worlds/worldzones/";
        var zones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in backend.Entries)
        {
            var dir = entry.Directory.Replace('\\', '/');
            if (!dir.StartsWith(ZonesRoot, StringComparison.OrdinalIgnoreCase))
                continue;
            var rest = dir[ZonesRoot.Length..];
            var slash = rest.IndexOf('/');
            zones.Add(slash >= 0 ? rest[..slash] : rest);
        }

        var zone = SelectMissionBaseZone(mainEntry.Name, zones);
        if (zone == null)
            return [];

        var zoneDir = ZonesRoot + zone;
        return backend.Entries
            .Where(entry =>
                string.Equals(entry.Directory.Replace('\\', '/'), zoneDir, StringComparison.OrdinalIgnoreCase)
                && IsSiblingPakFileName(entry.Name, zone))
            .OrderBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
        foreach (var candidate in Directory.EnumerateFiles(
                     dir,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            if (IsSiblingPakFileName(Path.GetFileName(candidate), stem))
                Add(candidate);
        }

        // Mission paks pool their base zone's dictionaries last (see the
        // archive-nested gather for the rationale).
        foreach (var zonePak in GetMissionBaseZonePaksOnDisk(pakPath))
            Add(zonePak);

        return result;
    }

    private static List<string> GetMissionBaseZonePaksOnDisk(string pakPath)
    {
        var fileName = Path.GetFileName(pakPath);
        if (!IsMissionPakFileName(fileName))
            return [];

        var dir = Path.GetDirectoryName(Path.GetFullPath(pakPath));
        for (var depth = 0; dir != null && depth < 6; depth++, dir = Path.GetDirectoryName(dir))
        {
            var zonesRoot = Path.Combine(dir, "worlds", "worldzones");
            if (!Directory.Exists(zonesRoot))
                continue;

            var zone = SelectMissionBaseZone(
                fileName,
                Directory.EnumerateDirectories(zonesRoot).Select(static d => Path.GetFileName(d)!));
            if (zone == null)
                return [];

            return Directory.EnumerateFiles(Path.Combine(zonesRoot, zone), "*", SearchOption.TopDirectoryOnly)
                .Where(f => IsSiblingPakFileName(Path.GetFileName(f), zone))
                .OrderBy(static f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return [];
    }

    internal static bool IsMissionPakFileName(string fileName)
    {
        return fileName.StartsWith("m_z", StringComparison.OrdinalIgnoreCase)
               && fileName.EndsWith(".pak.ps2", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Maps a mission pak name to its base zone by longest underscore-stripped
    ///     zone-name prefix: "m_zbhgaps4_success" → key "zbhgaps4_success" starts
    ///     with "zbh" (z_bh) but not "zbhsm" (z_bhsm). Census 2026-08-19: all 218
    ///     missions/worldzones families map to exactly one existing zone this way.
    /// </summary>
    internal static string? SelectMissionBaseZone(string pakFileName, IEnumerable<string> zoneDirNames)
    {
        var dot = pakFileName.IndexOf('.');
        var stem = dot > 0 ? pakFileName[..dot] : pakFileName;
        if (!stem.StartsWith("m_z", StringComparison.OrdinalIgnoreCase))
            return null;

        var key = stem[2..];
        string? best = null;
        var bestLength = 0;
        foreach (var zone in zoneDirNames)
        {
            if (zone == null || !zone.StartsWith("z_", StringComparison.OrdinalIgnoreCase))
                continue;
            var stripped = zone.Replace("_", "");
            if (stripped.Length > bestLength &&
                key.StartsWith(stripped, StringComparison.OrdinalIgnoreCase))
            {
                best = zone;
                bestLength = stripped.Length;
            }
        }

        return best;
    }

    internal static bool IsSiblingPakFileName(string fileName, string zoneStem)
    {
        const string PakPs2Suffix = ".pak.ps2";
        if (!fileName.EndsWith(PakPs2Suffix, StringComparison.OrdinalIgnoreCase))
            return false;

        var candidateStem = fileName[..^PakPs2Suffix.Length];
        return string.Equals(candidateStem, zoneStem, StringComparison.OrdinalIgnoreCase)
               || candidateStem.StartsWith(zoneStem + "_", StringComparison.OrdinalIgnoreCase);
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
}
