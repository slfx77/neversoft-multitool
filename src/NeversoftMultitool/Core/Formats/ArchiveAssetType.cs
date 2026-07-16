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
    Cut
}
