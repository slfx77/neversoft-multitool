using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.Vid1;

namespace NeversoftMultitool.Core;

internal static class FormatProbeAudio
{
    public static FormatProbe.FormatProbeResult Probe(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return ext switch
        {
            ".adx" => ProbeAdxFile(filePath),
            ".xa" => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "XA Audio"),
            ".vab" => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "VAB Sound Bank"),
            ".vag" => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "VAG Audio"),
            ".kat" => new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "KAT Sound Bank"),
            ".sfx" => ProbeSfxFile(filePath),
            ".pss" => ProbePssFile(filePath),
            ".vid" => ProbeVidFile(filePath),
            _ => ProbeHeaderlessAudio(filePath)
        };
    }

    private static FormatProbe.FormatProbeResult ProbeAdxFile(string filePath)
    {
        if (!BinaryProbeReader.TryReadHeader(filePath, 8, out var header, out var bytesRead))
            return HeaderReadFailure();

        if (bytesRead < 6)
            return FileTooSmall();

        if (header[0] == 0x80 && header[1] == 0x00)
        {
            var encoding = header[4];
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

    private static FormatProbe.FormatProbeResult ProbeSfxFile(string filePath)
    {
        return SfxExtractor.CanExtract(filePath, out var error)
            ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "SFX Cue Bank")
            : new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Unsupported, "SFX Cue Bank", error);
    }

    private static FormatProbe.FormatProbeResult ProbePssFile(string filePath)
    {
        return PssAudioExtractor.Probe(filePath) != null
            ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "PSS Audio")
            : new FormatProbe.FormatProbeResult(
                FormatProbe.FormatSupport.Unsupported,
                "PSS Audio",
                "PSS private-stream audio was not found");
    }

    private static FormatProbe.FormatProbeResult ProbeVidFile(string filePath)
    {
        return Vid1AudioExtractor.TryProbe(filePath, out _, out var error)
            ? new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Supported, "VID1 Audio")
            : new FormatProbe.FormatProbeResult(FormatProbe.FormatSupport.Unsupported, "VID1 Audio", error);
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

    private static FormatProbe.FormatProbeResult HeaderReadFailure()
    {
        return new FormatProbe.FormatProbeResult(
            FormatProbe.FormatSupport.Unsupported,
            "Unknown",
            "Failed to read file header");
    }
}
