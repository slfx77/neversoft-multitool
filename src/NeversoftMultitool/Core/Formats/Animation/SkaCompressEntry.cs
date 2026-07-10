namespace NeversoftMultitool.Core.Formats.Animation;

internal readonly struct SkaCompressEntry(short x, short y, short z, short scalar = 0)
{
    public short X { get; } = x;
    public short Y { get; } = y;
    public short Z { get; } = z;

    /// <summary>
    ///     Fourth s16 column of the table entry. THAW-era anims (flags bit 16)
    ///     use it as a per-component scalar lookup for byte-width components.
    /// </summary>
    public short Scalar { get; } = scalar;
}
