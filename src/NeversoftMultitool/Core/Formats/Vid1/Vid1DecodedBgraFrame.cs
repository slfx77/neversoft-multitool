namespace NeversoftMultitool.Core.Formats.Vid1;

internal sealed record Vid1DecodedBgraFrame(
    int FrameIndex,
    int Width,
    int Height,
    byte[] Bgra8);
