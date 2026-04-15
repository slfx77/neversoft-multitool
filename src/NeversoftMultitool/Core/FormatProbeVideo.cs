using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Core;

internal static class FormatProbeVideo
{
    public static FormatProbe.FormatProbeResult Probe(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return ext switch
        {
            ".sfd" => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "SFD Video"),
            ".pss" or ".PSS" => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "PSS Video"),
            ".bik" or ".BIK" => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "BIK Video"),
            ".vid" or ".VID" => ProbeVidFile(filePath),
            ".str" => ProbeStrFile(filePath),
            _ => new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Unknown",
                $"Unrecognized video format: {ext}")
        };
    }

    private static FormatProbe.FormatProbeResult ProbeVidFile(string filePath)
    {
        var probe = Vid1VideoConverter.Probe(filePath);
        return probe != null
            ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "VID1 Video")
            : new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "VID1 Video",
                "Not a valid VID1 video");
    }

    private static FormatProbe.FormatProbeResult ProbeStrFile(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (info.Length == 0)
                return new FormatProbe.FormatProbeResult(
                    FormatProbe.FormatSupport.Unsupported, "Unknown", "Empty file");

            // Read first 12 bytes to detect format variant
            if (!BinaryProbeReader.TryReadHeader(filePath, 12, out var header, out var bytesRead) || bytesRead < 12)
            {
                return new FormatProbe.FormatProbeResult(
                    FormatProbe.FormatSupport.Unsupported, "Unknown", "File too small");
            }

            // RIFF/CDXA container: "RIFF....CDXA" header + 2352-byte raw sectors
            if (header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F'
                && header[8] == 'C' && header[9] == 'D' && header[10] == 'X' && header[11] == 'A'
                && (info.Length - 44) % 2352 == 0)
                return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "STR Video (RIFF/CDXA)");

            // Standard 2336-byte sectors
            if (info.Length % 2336 == 0)
                return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "STR Video");

            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported, "Unknown",
                "Not a valid STR video (unrecognized sector layout)");
        }
        catch
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported, "Unknown", "Failed to read file");
        }
    }
}
