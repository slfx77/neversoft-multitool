namespace NeversoftMultitool.Core.Formats.Video;

internal sealed record Vid1DecodedBgraFrame(
    int FrameIndex,
    int Width,
    int Height,
    byte[] Bgra8);
