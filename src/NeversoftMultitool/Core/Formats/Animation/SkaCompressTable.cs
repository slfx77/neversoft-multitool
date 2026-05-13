namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Q48/T48 compression lookup tables (256 entries each).
///     Loaded from external files, shared across all animations.
/// </summary>
internal sealed class SkaCompressTable
{
    public required SkaCompressEntry[] Q48 { get; init; }
    public required SkaCompressEntry[] T48 { get; init; }

    /// <summary>
    ///     Load Q48/T48 tables from raw binary files.
    ///     Each file is 2048 bytes (256 entries × 8 bytes: x48(s16) + y48(s16) + z48(s16) + n8(s16)).
    /// </summary>
    internal static SkaCompressTable? TryLoad(string q48Path, string t48Path)
    {
        if (!File.Exists(q48Path) || !File.Exists(t48Path))
            return null;

        var q48Data = File.ReadAllBytes(q48Path);
        var t48Data = File.ReadAllBytes(t48Path);

        if (q48Data.Length < 2048 || t48Data.Length < 2048)
            return null;

        return new SkaCompressTable
        {
            Q48 = ParseEntries(q48Data),
            T48 = ParseEntries(t48Data)
        };
    }

    private static SkaCompressEntry[] ParseEntries(byte[] data)
    {
        var entries = new SkaCompressEntry[256];
        for (var i = 0; i < 256; i++)
        {
            var off = i * 8;
            entries[i] = new SkaCompressEntry(
                BitConverter.ToInt16(data, off),
                BitConverter.ToInt16(data, off + 2),
                BitConverter.ToInt16(data, off + 4));
        }

        return entries;
    }
}
