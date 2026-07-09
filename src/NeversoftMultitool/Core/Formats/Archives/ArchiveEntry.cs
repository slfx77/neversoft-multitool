namespace NeversoftMultitool.Core.Formats.Archives;

/// <summary>
///     Represents a single file entry within an archive (WAD, PKR, PRE, DDX, or BON).
/// </summary>
public sealed class ArchiveEntry
{
    public string Name { get; set; } = "";
    public string Directory { get; set; } = "";
    public long Size { get; set; }
    public long Offset { get; set; }
    public bool IsCompressed { get; set; }
    public long CompressedSize { get; set; }
    public uint Crc { get; set; }

    /// <summary>
    ///     True when the entry's data lives in a companion data file
    ///     (GC PAK entries without the 0x80000000 in-pak flag).
    /// </summary>
    public bool InCompanion { get; set; }

    /// <summary>
    ///     Full path including directory prefix.
    /// </summary>
    public string FullName => string.IsNullOrEmpty(Directory) ? Name : $"{Directory}/{Name}";
}
