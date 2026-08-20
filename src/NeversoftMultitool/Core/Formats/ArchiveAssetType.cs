namespace NeversoftMultitool.Core.Formats;

/// <summary>
///     Archive backends the converter tabs can enumerate and read entries from
///     without a temp-extract step. Detection lives in
///     <see cref="Archives.ArchiveTypeDetector" />.
/// </summary>
public enum ArchiveAssetType
{
    Wad,
    Pre,
    CompressedPre,
    Pkr,
    Pak,
    Ddx,
    Bon,
    Zip,
    Cut,

    /// <summary>
    ///     N64 .z64 ROM opened as its carved asset tree. Entries are
    ///     decompress+reassemble+carve products (not byte ranges of the ROM),
    ///     so the backend materializes them at open.
    /// </summary>
    N64,

    /// <summary>
    ///     Nintendo DS .nds ROM opened as its Nitro filesystem. Entries are plain
    ///     contiguous byte ranges (FAT start/end), so the disk-backed backend
    ///     serves them with no carve/decompress step.
    /// </summary>
    Nds
}
