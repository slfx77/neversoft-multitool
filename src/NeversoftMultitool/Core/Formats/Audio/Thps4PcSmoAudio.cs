namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>Metadata for one THPS4-PC <c>.smo</c> Bink-DCT soundtrack.</summary>
public sealed record Thps4PcSmoAudioProbeResult(
    int SampleRate,
    int Channels,
    uint FrameCount,
    double DurationSeconds,
    uint LargestFrameSize,
    uint MaximumDecodedAudioSize);

/// <summary>
///     Strict reader and WAV converter for THPS4 PC soundtrack carriers. These
///     are complete BIKi containers, not SMO metadata; an exact 4x4 placeholder
///     video and stereo 44.1/48 kHz audio profile keeps normal movies out.
/// </summary>
public static class Thps4PcSmoAudio
{
    public static Thps4PcSmoAudioProbeResult? Probe(string inputPath)
    {
        try
        {
            return Probe(File.ReadAllBytes(inputPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static Thps4PcSmoAudioProbeResult? Probe(ReadOnlySpan<byte> data)
    {
        var info = BinkAudioCarrier.Probe(data, BinkAudioCarrierProfile.Thps4Smo);
        return info == null
            ? null
            : new Thps4PcSmoAudioProbeResult(
                info.SampleRate,
                info.Channels,
                info.FrameCount,
                info.DurationSeconds,
                info.LargestFrameSize,
                info.MaximumDecodedAudioSize);
    }

    public static AudioConvertResult ConvertToWav(string inputPath, string outputDirectory)
    {
        return ConvertToWav(
            inputPath,
            Path.GetFileNameWithoutExtension(inputPath),
            outputDirectory);
    }

    public static AudioConvertResult ConvertToWav(
        string inputPath,
        string outputStem,
        string outputDirectory)
    {
        try
        {
            var probe = Probe(inputPath);
            return probe == null
                ? NotThisFormat()
                : StrictFfmpegAudioConverter.ConvertPath(
                    inputPath,
                    outputStem,
                    outputDirectory,
                    probe.SampleRate,
                    probe.Channels,
                    "SMO");
        }
        catch (Exception ex)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
    }

    public static AudioConvertResult ConvertToWav(
        byte[] data,
        string outputStem,
        string outputDirectory)
    {
        return ConvertToWav(data, outputStem, outputDirectory, null);
    }

    internal static AudioConvertResult ConvertToWav(
        byte[] data,
        string outputStem,
        string outputDirectory,
        AudioPcmTranscoder? transcoder)
    {
        ArgumentNullException.ThrowIfNull(data);
        var probe = Probe(data);
        return probe == null
            ? NotThisFormat()
            : StrictFfmpegAudioConverter.ConvertBytes(
                data,
                ".smo",
                outputStem,
                outputDirectory,
                probe.SampleRate,
                probe.Channels,
                "SMO",
                transcoder);
    }

    private static AudioConvertResult NotThisFormat()
    {
        return new AudioConvertResult
        {
            Skipped = true,
            ErrorMessage = "Not an exact THPS4 PC BIKi SMO audio carrier"
        };
    }
}
