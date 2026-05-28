namespace NeversoftMultitool.Core.Formats.Vid1;

/// <summary>
///     A single decoded frame from a VID1 stream, packed as top-down RGB24
///     (row-major, 3 bytes per pixel, no row padding).
/// </summary>
public sealed record Vid1DecodedFrame(
    int FrameIndex,
    int Width,
    int Height,
    byte[] Rgb24);
