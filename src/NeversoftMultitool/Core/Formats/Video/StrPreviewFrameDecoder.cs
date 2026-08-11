namespace NeversoftMultitool.Core.Formats.Video;

/// <summary>
///     Decodes STR video frames into the BGRA8 layout used by direct preview.
///     Corrupt frames become opaque black so one bad frame does not terminate playback.
/// </summary>
internal static class StrPreviewFrameDecoder
{
    internal static byte[] DecodeBgra8OrBlack(byte[] frameData, int width, int height)
    {
        var pixelCount = checked(width * height);
        byte[] rgb;

        try
        {
            rgb = MdecDecoder.DecodeFrame(frameData, width, height);
        }
        catch
        {
            return CreateOpaqueBlackFrame(pixelCount);
        }

        var bgra = new byte[checked(pixelCount * 4)];
        for (var i = 0; i < pixelCount; i++)
        {
            var srcIdx = i * 3;
            var dstIdx = i * 4;
            bgra[dstIdx] = rgb[srcIdx + 2];
            bgra[dstIdx + 1] = rgb[srcIdx + 1];
            bgra[dstIdx + 2] = rgb[srcIdx];
            bgra[dstIdx + 3] = 0xFF;
        }

        return bgra;
    }

    private static byte[] CreateOpaqueBlackFrame(int pixelCount)
    {
        var bgra = new byte[checked(pixelCount * 4)];
        for (var i = 3; i < bgra.Length; i += 4)
            bgra[i] = 0xFF;
        return bgra;
    }
}
