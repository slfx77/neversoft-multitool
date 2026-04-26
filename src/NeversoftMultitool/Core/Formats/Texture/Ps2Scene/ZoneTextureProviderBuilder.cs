using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;

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
    ///     Collects .tex / .tex.ps2 / .img.ps2 / .stex files reachable from <paramref name="path"/>.
    ///     When <paramref name="path"/> is a .pak.ps2 file, also includes sibling worldzone PAKs
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
        out Ps2SceneGltfWriter.TextureProvider? textureProvider,
        out Ps2GeomGltfWriter.Tex0Resolver? tex0Resolver,
        Action<string>? log = null)
    {
        textureProvider = null;
        tex0Resolver = null;

        if (texPath == null) return false;

        var texFiles = GetTexFiles(texPath);
        var textureCache = new Dictionary<uint, Ps2Texture>();
        var checksumByTbpCbp = new Dictionary<(uint Tbp, uint Cbp), uint>();
        // Higher-fidelity map keyed on TEX0 bits that affect the actual texture
        // lookup: TBP (0-13), PSM (20-25), TW (26-29), TH (30-33), CBP (37-50),
        // CPSM (51-54). Excludes TBW (stride) and TCC/TFX/CSM/CSA/CLD which are
        // rendering-state bits, not identity. This catches dimension + format
        // differences that a bare (TBP, CBP) key collapses together.
        var checksumByKey = new Dictionary<ulong, uint>();
        var zoneTexCount = 0;

        foreach (var tf in texFiles)
        {
            try
            {
                var data = File.ReadAllBytes(tf);

                // A .pak.ps2 file is an archive containing many .stex/.tex entries. Extracting
                // each entry separately yields far more textures than parsing the whole PAK as
                // a single ThawZoneTex blob.
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
                            textureCache, checksumByTbpCbp, checksumByKey, ref zoneTexCount, log);
                    }
                    if (ThawZoneTexFile.IsThawZoneTex(data))
                        ParseZoneTexBytes(data, Path.GetFileName(tf),
                            textureCache, checksumByTbpCbp, checksumByKey, ref zoneTexCount, log);
                }
                else if (ThawZoneTexFile.IsThawZoneTex(data))
                {
                    ParseZoneTexBytes(data, Path.GetFileName(tf),
                        textureCache, checksumByTbpCbp, checksumByKey, ref zoneTexCount, log);
                }
            }
            catch
            {
                // Skip unreadable files
            }
        }

        if (zoneTexCount == 0) return false;
        if (textureCache.Count == 0) return false;

        log?.Invoke($"Decoded {textureCache.Count} textures from {zoneTexCount} zone TEX file(s)");

        var pngCache = new Dictionary<uint, byte[]?>();

        tex0Resolver = (dmaTex0, _) =>
        {
            var tbp = (uint)(dmaTex0 & 0x3FFF);
            var cbp = (uint)((dmaTex0 >> 37) & 0x3FFF);
            var fullKey = MakeTex0IdentityKey(dmaTex0);
            var mapped = checksumByKey.TryGetValue(fullKey, out var ck)
                         || checksumByTbpCbp.TryGetValue((tbp, cbp), out ck);
            var checksum = mapped ? ck : (tbp << 16) | cbp;
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

    /// <summary>
    ///     Keep the TEX0 bits that identify a texture instance: TBP (base pointer
    ///     0-13), PSM (pixel format 20-25), TW (width exp 26-29), TH (height exp
    ///     30-33), CBP (CLUT pointer 37-50), CPSM (CLUT format 51-54). Strip the
    ///     rendering-state bits so two TEX0 writes that reference the same texture
    ///     under different render state collapse to one key.
    /// </summary>
    private static ulong MakeTex0IdentityKey(ulong tex0)
    {
        var tbp   =  tex0        & 0x3FFFUL;
        var psm   = (tex0 >> 20) & 0x3FUL;
        var tw    = (tex0 >> 26) & 0xFUL;
        var th    = (tex0 >> 30) & 0xFUL;
        var cbp   = (tex0 >> 37) & 0x3FFFUL;
        var cpsm  = (tex0 >> 51) & 0xFUL;
        return tbp | (psm << 14) | (tw << 20) | (th << 24) | (cbp << 28) | (cpsm << 42);
    }

    private static void ParseZoneTexBytes(
        byte[] data,
        string label,
        Dictionary<uint, Ps2Texture> textureCache,
        Dictionary<(uint Tbp, uint Cbp), uint> checksumByTbpCbp,
        Dictionary<ulong, uint> checksumByKey,
        ref int zoneTexCount,
        Action<string>? log)
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
            checksumByKey.TryAdd(MakeTex0IdentityKey(entry.Tex0), entry.Checksum);
        }

        log?.Invoke($"Detected zone TEX: {label} ({entries.Count} records, {textures.Count} textures)");
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
