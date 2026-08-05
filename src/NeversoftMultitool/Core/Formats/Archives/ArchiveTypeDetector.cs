using NeversoftMultitool.Core.Formats.DiscImage;
using N64 = NeversoftMultitool.Core.Formats.N64;

namespace NeversoftMultitool.Core.Formats.Archives;

/// <summary>
///     Single home for archive extension and type detection. Folds the previously
///     duplicated extension lists and classification switches (RecursiveUnpacker,
///     FormatProbeArchive, ArchiveAssetBackend, CLI/tab dispatchers) so every
///     consumer answers "what kind of archive is this?" the same way.
/// </summary>
public static class ArchiveTypeDetector
{
    // .img is gated on a same-stem .ccd sibling inside DiscImageArchive
    // because bare .img files are PS2 IOP modules / GC textures rather than
    // disc images. Bare .bin track files are reached through their .cue.
    public static readonly string[] ArchiveExtensions =
    [
        ".wad", ".pre", ".prx", ".prd", ".prf", ".prg", ".pkr", ".ddx", ".bon", ".pak", ".apk", ".zip", ".cut",
        ".iso", ".cue", ".gdi", ".img", ".z64"
    ];

    /// <summary>
    ///     Extensions that may appear as entries INSIDE another archive and can be
    ///     opened in place (nested filesystem). WAD is excluded (needs a sibling
    ///     .HED); ZIP/CUT/DDX/BON never nest in the corpus; disc images are
    ///     top-level containers only.
    /// </summary>
    private static readonly string[] NestedArchiveExtensions =
    [
        ".pre", ".prx", ".prd", ".prf", ".prg", ".pkr", ".pak", ".apk"
    ];

    /// <summary>
    ///     Gets the archive-relevant extension, handling double extensions like .pak.ps2.
    /// </summary>
    public static string GetArchiveExtension(string filePath)
    {
        var name = Path.GetFileName(filePath).ToLowerInvariant();

        // Check for double extensions (e.g. .pak.ps2, .pak.xen)
        foreach (var archiveExt in ArchiveExtensions)
        {
            var pattern = archiveExt + ".";
            var idx = name.IndexOf(pattern, StringComparison.Ordinal);
            if (idx > 0)
                return archiveExt;
        }

        return Path.GetExtension(filePath).ToLowerInvariant();
    }

    /// <summary>
    ///     Checks if a file path has a supported archive extension.
    /// </summary>
    public static bool IsArchiveFile(string filePath)
    {
        return ArchiveExtensions.Contains(GetArchiveExtension(filePath));
    }

    /// <summary>
    ///     Classifies an archive file into a display type string. Entries ending
    ///     in "(raw)" carry an archive extension but no parseable structure.
    /// </summary>
    public static string Classify(string filePath)
    {
        var ext = GetArchiveExtension(filePath);
        return ext switch
        {
            ".wad" => "WAD",
            ".pre" => CompressedPreArchive.IsCompressedPre(filePath) ? "PRE3" : "PRE",
            ".prx" => "PRE3",
            ".prd" or ".prg" => CompressedPreArchive.IsCompressedPre(filePath) ? "PRE3 (German)" : "PRE (German)",
            ".prf" => CompressedPreArchive.IsCompressedPre(filePath) ? "PRE3 (French)" : "PRE (French)",
            ".pkr" => "PKR",
            ".ddx" => "DDX",
            ".bon" => "BON",
            ".pak" => PakArchive.IsPakArchive(filePath) ? "PAK" : "PAK (raw)",
            ".apk" => PakArchive.IsPakArchive(filePath) ? "PAK (GC)" : "PAK (raw)",
            ".zip" => QZipArchive.IsZip(filePath) ? "ZIP" : "ZIP (raw)",
            ".cut" => CutArchive.IsCut(filePath) ? "CUT" : "CUT (raw)",
            ".iso" or ".cue" or ".gdi" or ".img" =>
                DiscImageArchive.IsDiscImage(filePath) ? "DISC" : "DISC (raw)",
            ".z64" => N64.N64RomArchive.ClassifyRom(filePath) ?? "N64 ROM (raw)",
            _ => "?"
        };
    }

    /// <summary>
    ///     Content+extension detection for on-disk archives the asset filesystem
    ///     can enumerate. Returns null for non-archives, raw-data files, and disc
    ///     images (which have their own sector-source pipeline).
    /// </summary>
    public static ArchiveAssetType? DetectAssetType(string path)
    {
        return GetArchiveExtension(path) switch
        {
            // WAD needs a sibling .HED — without it, the listing is unreadable.
            ".wad" => File.Exists(WadArchive.GetHedPath(path)) ? ArchiveAssetType.Wad : null,
            ".prx" => ArchiveAssetType.CompressedPre,
            ".pre" or ".prd" or ".prf" or ".prg" => CompressedPreArchive.IsCompressedPre(path)
                ? ArchiveAssetType.CompressedPre
                : ArchiveAssetType.Pre,
            ".pkr" => ArchiveAssetType.Pkr,
            ".pak" or ".apk" => PakArchive.IsPakArchive(path) ? ArchiveAssetType.Pak : null,
            ".ddx" => ArchiveAssetType.Ddx,
            ".bon" => ArchiveAssetType.Bon,
            ".zip" => QZipArchive.IsZip(path) ? ArchiveAssetType.Zip : null,
            ".cut" => CutArchive.IsCut(path) ? ArchiveAssetType.Cut : null,
            _ => null
        };
    }

    /// <summary>
    ///     Detection for an entry nested inside another archive, judged from the
    ///     entry name plus its already-read bytes (nested archives have no disk
    ///     path to probe).
    /// </summary>
    public static ArchiveAssetType? DetectNestedAssetType(string entryName, byte[] data)
    {
        var ext = GetArchiveExtension(entryName);
        if (!NestedArchiveExtensions.Contains(ext))
            return null;

        return ext switch
        {
            ".prx" => ArchiveAssetType.CompressedPre,
            ".pre" or ".prd" or ".prf" or ".prg" => CompressedPreArchive.IsCompressedPre(data.AsSpan())
                ? ArchiveAssetType.CompressedPre
                : ArchiveAssetType.Pre,
            ".pkr" => ArchiveAssetType.Pkr,
            ".pak" or ".apk" => PakArchive.IsPakArchive(data) ? ArchiveAssetType.Pak : null,
            _ => null
        };
    }
}
