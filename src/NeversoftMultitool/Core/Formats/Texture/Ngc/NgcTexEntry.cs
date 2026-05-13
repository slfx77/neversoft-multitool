namespace NeversoftMultitool.Core.Formats.Texture.Ngc;

internal readonly record struct NgcTexEntry(
    uint Magic,
    uint Checksum,
    int Width,
    int Height,
    byte FormatA,
    byte FormatB,
    int DataSize,
    int DataOffset);
