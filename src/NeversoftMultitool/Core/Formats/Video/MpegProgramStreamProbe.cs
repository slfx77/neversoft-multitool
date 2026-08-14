namespace NeversoftMultitool.Core.Formats.Video;

/// <summary>
///     Performs a bounded MPEG program-stream pack-header check for directory scans.
/// </summary>
internal static class MpegProgramStreamProbe
{
    private const int Mpeg1PackHeaderSize = 12;
    private const int Mpeg2PackHeaderSize = 14;

    public static bool HasPackHeader(string path)
    {
        if (!BinaryProbeReader.TryReadHeader(path, Mpeg2PackHeaderSize, out var header, out var bytesRead))
            return false;

        var data = header.AsSpan(0, bytesRead);
        if (data.Length < 5 ||
            data[0] != 0x00 || data[1] != 0x00 || data[2] != 0x01 || data[3] != 0xBA)
        {
            return false;
        }

        var versionByte = data[4];
        if ((versionByte & 0xF0) == 0x20)
            return data.Length >= Mpeg1PackHeaderSize;

        return (versionByte & 0xC0) == 0x40 && data.Length >= Mpeg2PackHeaderSize;
    }
}
