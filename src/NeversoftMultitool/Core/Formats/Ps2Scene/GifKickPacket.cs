namespace NeversoftMultitool.Core.Formats.Ps2Scene;

internal sealed class GifKickPacket
{
    public static GifKickPacket None { get; } = new()
    {
        Kind = GifKickPacketKind.None
    };

    public GifKickPacketKind Kind { get; init; }
    public bool IsPresent => Kind != GifKickPacketKind.None;
    public bool Eop { get; init; }
    public int Nloop { get; init; }
    public int Address { get; init; }
    public int Size { get; init; }
    public int SourceElementIndex { get; init; }
}
