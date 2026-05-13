namespace NeversoftMultitool.Core.Formats;

/// <summary>
///     Archive backends the converter tabs can enumerate and read entries from
///     without a temp-extract step. Keep this in sync with
///     <see cref="ArchiveAssetBackend" />'s type-detection logic.
/// </summary>
public enum ArchiveAssetType
{
    Wad,
    Pre,
    CompressedPre,
    Pkr,
    Pak
}
