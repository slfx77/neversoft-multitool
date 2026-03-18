namespace NeversoftMultitool.Core;

internal static class FormatProbeAudio
{
    public static FormatProbe.FormatProbeResult Probe(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".adx" => ProbeAdxFile(filePath),
            ".xa" => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "XA Audio"),
            ".vab" => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "VAB Sound Bank"),
            ".vag" => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "VAG Audio"),
            ".kat" => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "KAT Sound Bank"),
            ".pss" => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "PSS Audio"),
            _ => ProbeHeaderlessAudio(filePath)
        };
    }

    private static FormatProbe.FormatProbeResult ProbeAdxFile(string filePath)
    {
        try
        {
            var data = new byte[8];
            using var fs = File.OpenRead(filePath);
            var bytesRead = fs.Read(data, 0, data.Length);
            if (bytesRead < 6)
                return FileTooSmall();

            if (data[0] == 0x80 && data[1] == 0x00)
            {
                var encoding = data[4];
                return encoding == 3
                    ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "ADX Audio")
                    : new FormatProbe.FormatProbeResult(
                        FormatProbe.FormatSupport.Unsupported,
                        "ADX Audio",
                        $"Unsupported ADX encoding type {encoding} (only type 3 supported)");
            }

            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Unknown",
                "Not a valid ADX file (missing 0x8000 magic)");
        }
        catch
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Unknown",
                "Failed to read file header");
        }
    }

    private static FormatProbe.FormatProbeResult ProbeHeaderlessAudio(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            return info.Length > 0 && info.Length % 16 == 0
                ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "Headerless SPU-ADPCM")
                : new FormatProbe.FormatProbeResult(
                    FormatProbe.FormatSupport.Unsupported,
                    "Unknown",
                    $"Unrecognized audio format: {Path.GetExtension(filePath)}");
        }
        catch
        {
            return new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "Unknown",
                "Failed to read file");
        }
    }

    private static FormatProbe.FormatProbeResult FileTooSmall()
    {
        return new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Unsupported, "Unknown", "File too small");
    }
}
