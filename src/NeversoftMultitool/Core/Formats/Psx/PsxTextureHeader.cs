namespace NeversoftMultitool.Core.Formats.Psx;

public sealed class PsxTextureHeader
{
    public uint Unk { get; set; }
    public uint PalSize { get; set; }
    public uint Hash { get; set; }
    public uint Index { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public uint PixelFormat { get; set; }
    public uint Size { get; set; }
    public long Offset { get; set; }
    public long TextureOffset { get; set; }
}
