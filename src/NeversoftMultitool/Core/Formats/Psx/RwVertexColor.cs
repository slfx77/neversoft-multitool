namespace NeversoftMultitool.Core.Formats.Psx;

public readonly struct RwVertexColor(byte r, byte g, byte b, byte a)
{
    public byte R { get; } = r;
    public byte G { get; } = g;
    public byte B { get; } = b;
    public byte A { get; } = a;
}
