namespace NeversoftMultitool.Core;

internal static class FormatProbeVideo
{
    public static FormatProbe.FormatProbeResult Probe(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".sfd" => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "SFD Video"),
            ".str" => ProbeStrFile(filePath),
            _ => new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Unknown",
                $"Unrecognized video format: {ext}")
        };
    }

    private static FormatProbe.FormatProbeResult ProbeStrFile(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (info.Length == 0 || info.Length % 2336 != 0)
            {
                return new FormatProbe.FormatProbeResult(
                    FormatProbe.FormatSupport.Unsupported,
                    "Unknown",
                    "Not a valid STR video (file size not a multiple of 2336-byte sectors)");
            }

            var data = new byte[8];
            using var fs = File.OpenRead(filePath);
            var bytesRead = fs.Read(data, 0, data.Length);
            if (bytesRead < data.Length)
                return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Unsupported, "Unknown", "File too small");

            return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "STR Video");
        }
        catch
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Unknown",
                "Failed to read file");
        }
    }
}
