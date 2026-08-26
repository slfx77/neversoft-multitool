using NeversoftMultitool.Core.Formats.DiscImage;
using Gob = NeversoftMultitool.Core.Formats.Gob;
using N64 = NeversoftMultitool.Core.Formats.N64;
using Nds = NeversoftMultitool.Core.Formats.Nds;

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
        ".iso", ".cue", ".gdi", ".img", ".z64", ".nds", ".gob", ".sdat", ".gba"
    ];

    /// <summary>
    ///     Extensions that may appear as entries INSIDE another archive and can be
    ///     opened in place (nested filesystem). WAD is excluded (needs a sibling
    ///     .HED); ZIP/CUT/DDX/BON never nest in the corpus; disc images are
    ///     top-level containers only.
    /// </summary>
    private static readonly string[] NestedArchiveExtensions =
    [
        ".pre", ".prx", ".prd", ".prf", ".prg", ".pkr", ".pak", ".apk", ".gob", ".sdat"
    ];

    /// <summary>
    ///     Whether an entry with this name is worth opening as a container of its own.
    ///     The single source of truth for the question — a second copy of the list is
    ///     how a nestable type ends up reachable from one caller and invisible to
    ///     another.
    /// </summary>
    public static bool CanNest(string entryName)
    {
        return NestedArchiveExtensions.Contains(GetArchiveExtension(entryName));
    }

    /// <summary>
    ///     Gets the archive-relevant extension, handling double extensions like .pak.ps2.
    /// </summary>
    public static string GetArchiveExtension(string filePath)
    {
        var finalExtension = Path.GetExtension(filePath).ToLowerInvariant();

        // A supported final extension is the outer container. Compound scanning
        // is only for platform suffixes such as .pak.ps2 and .zip.wpc.
        if (ArchiveExtensions.Contains(finalExtension))
            return finalExtension;

        // Check the immediately preceding extension for platform-qualified
        // archives (e.g. .pak.ps2, .zip.wpc).
        var precedingExtension = Path.GetExtension(
            Path.GetFileNameWithoutExtension(filePath)).ToLowerInvariant();
        if (ArchiveExtensions.Contains(precedingExtension))
            return precedingExtension;

        return finalExtension;
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
            ".nds" => Nds.NdsRomArchive.ClassifyRom(filePath) ?? "NDS ROM (raw)",
            ".gob" => Gob.GobArchive.ClassifyArchive(filePath) ?? "GOB (raw)",
            ".sdat" => Nds.SdatArchive.ClassifyArchive(filePath) ?? "SDAT (raw)",
            ".gba" => Gba.GbaLevelCarver.ClassifyRom(filePath) ?? "GBA ROM (raw)",
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
            ".prx" => File.Exists(path) && CompressedPreArchive.IsCompressedPre(path)
                ? ArchiveAssetType.CompressedPre
                : null,
            ".pre" or ".prd" or ".prf" or ".prg" => CompressedPreArchive.IsCompressedPre(path)
                ? ArchiveAssetType.CompressedPre
                : ArchiveAssetType.Pre,
            ".pkr" => ArchiveAssetType.Pkr,
            ".pak" or ".apk" => PakArchive.IsPakArchive(path) ? ArchiveAssetType.Pak : null,
            ".ddx" => ArchiveAssetType.Ddx,
            ".bon" => ArchiveAssetType.Bon,
            ".zip" => QZipArchive.IsZip(path) ? ArchiveAssetType.Zip : null,
            ".cut" => CutArchive.IsCut(path) ? ArchiveAssetType.Cut : null,
            ".z64" => N64.N64RomArchive.IsN64Rom(path) ? ArchiveAssetType.N64 : null,
            ".nds" => Nds.NdsRomArchive.IsNdsRom(path) ? ArchiveAssetType.Nds : null,
            // GOB needs its sibling .gfc index — without it the blob is unreadable.
            ".gob" => Gob.GobArchive.IsGobArchive(path) ? ArchiveAssetType.Gob : null,
            ".sdat" => Nds.SdatArchive.IsSdat(path) ? ArchiveAssetType.Sdat : null,
            // Only VV carts with the level table carve; other GBA ROMs stay raw.
            ".gba" => Gba.GbaLevelCarver.IsVvLevelRom(path) ? ArchiveAssetType.Gba : null,
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
            ".prx" => CompressedPreArchive.IsCompressedPre(data.AsSpan())
                ? ArchiveAssetType.CompressedPre
                : null,
            ".pre" or ".prd" or ".prf" or ".prg" => CompressedPreArchive.IsCompressedPre(data.AsSpan())
                ? ArchiveAssetType.CompressedPre
                : ArchiveAssetType.Pre,
            ".pkr" => ArchiveAssetType.Pkr,
            ".pak" or ".apk" => PakArchive.IsPakArchive(data) ? ArchiveAssetType.Pak : null,
            // The .gob blob carries no header of its own; whether it is really a GOB
            // is settled against the companion .gfc in ArchiveFileSystem.TryOpenNested.
            ".gob" => ArchiveAssetType.Gob,
            _ => null
        };
    }
            // A DS cart's soundtrack is an SDAT sitting inside the cart's own file
            // table, so it only ever appears nested. The unpacker already walks
            // cart -> GOB -> SDAT; this lets a browsing caller do the same.
            ".sdat" => Nds.SdatArchive.IsSdat(data.AsSpan()) ? ArchiveAssetType.Sdat : null,
}
