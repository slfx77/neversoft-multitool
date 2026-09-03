namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>Metadata for one THPS4-PC <c>Snd*.dee</c> Bink-DCT sound.</summary>
public sealed record Thps4PcDeeAudioProbeResult(
    int SampleRate,
    int Channels,
    uint FrameCount,
    double DurationSeconds,
    uint LargestFrameSize,
    uint MaximumDecodedAudioSize);

/// <summary>
///     Strict reader and WAV converter for THPS4 PC's <c>Snd*.dee</c> audio
///     carriers. DEE is not a sidecar or an index: every measured file is a
///     complete BIKi container with a 4x4 placeholder video and one Bink-DCT
///     audio track.
/// </summary>
public static class Thps4PcDeeAudio
{
    public static Thps4PcDeeAudioProbeResult? Probe(string inputPath)
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

    public static Thps4PcDeeAudioProbeResult? Probe(ReadOnlySpan<byte> data)
    {
        return Map(BinkAudioCarrier.Probe(data, BinkAudioCarrierProfile.Thps4Dee));
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
                    "DEE");
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
                ".dee",
                outputStem,
                outputDirectory,
                probe.SampleRate,
                probe.Channels,
                "DEE",
                transcoder);
    }

    private static Thps4PcDeeAudioProbeResult? Map(BinkAudioCarrierInfo? info)
    {
        return info == null
            ? null
            : new Thps4PcDeeAudioProbeResult(
                info.SampleRate,
                info.Channels,
                info.FrameCount,
                info.DurationSeconds,
                info.LargestFrameSize,
                info.MaximumDecodedAudioSize);
    }

    private static AudioConvertResult NotThisFormat()
    {
        return new AudioConvertResult
        {
            Skipped = true,
            ErrorMessage = "Not an exact THPS4 PC BIKi DEE audio carrier"
        };
    }
}
