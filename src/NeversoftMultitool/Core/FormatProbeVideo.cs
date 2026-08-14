using NeversoftMultitool.Core.Formats.Vid1;
using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Core;

internal static class FormatProbeVideo
{
    public static FormatProbe.FormatProbeResult Probe(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return ext.ToLowerInvariant() switch
        {
            ".sfd" => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "SFD Video"),
            ".pss" => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "PSS Video"),
            ".bik" => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "BIK Video"),
            ".vid" => ProbeVidFile(filePath),
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

            // RIFF/CDXA container: "RIFF....CDXA" header + 2352-byte raw sectors.
            // Layout identifies the variant label, but it does not prove that the
            // sectors contain video (an aligned all-zero file used to pass here).
            var isRiffCdxa = header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F'
                             && header[8] == 'C' && header[9] == 'D' && header[10] == 'X' && header[11] == 'A'
                             && (info.Length - 44) % 2352 == 0;
            var hasStandardLayout = info.Length % 2336 == 0;
            if (!isRiffCdxa && !hasStandardLayout)
            {
                return new FormatProbe.FormatProbeResult(
                    FormatProbe.FormatSupport.Unsupported, "Unknown",
                    "Not a valid STR video (unrecognized sector layout)");
            }

            // Match the advertised conversion path: a supported STR must contain
            // at least one complete frame, not merely occupy whole sector slots.
            if (StrConverter.Probe(filePath) == null)
            {
                return new FormatProbe.FormatProbeResult(
                    FormatProbe.FormatSupport.Unsupported, "Unknown",
                    "Not a valid STR video (no complete video frame)");
            }

            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Supported,
                isRiffCdxa ? "STR Video (RIFF/CDXA)" : "STR Video");
        }
        catch
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported, "Unknown", "Failed to read file");
        }
    }
}
