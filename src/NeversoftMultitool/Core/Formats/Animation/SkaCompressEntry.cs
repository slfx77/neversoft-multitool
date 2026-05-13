namespace NeversoftMultitool.Core.Formats.Animation;

internal readonly struct SkaCompressEntry(short x, short y, short z)
{
    public short X { get; } = x;
    public short Y { get; } = y;
    public short Z { get; } = z;
}
