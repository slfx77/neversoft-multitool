using NeversoftMultitool.Core.Formats.Vid1;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Reads the duration of the decoded timeline represented by one Audio-tab
///     parent row. Sound banks intentionally return no aggregate duration: their
///     samples are independent timelines.
/// </summary>
public static class AudioDurationProbe
{
    private const int XaSoundGroupSize = 128;
    private const int XaRawFramesPerGroup = 4 * 28;
    private const int XaRawSampleRate = 37800;

    public static double? Probe(string audioFormat, byte[] data)
    {
        try
        {
            return audioFormat.ToUpperInvariant() switch
            {
                "ADX" => AdxDecoder.Probe(data)?.DurationSeconds,
                "XA" => ProbeXa(data),
                "VAG" => VagDecoder.Probe(data)?.DurationSeconds,
                "PCM" => XboxPcmDecoder.Probe(data)?.DurationSeconds,
                "SWAV" or "STRM" or "HWAS" => NdsAudioDecoder.Probe(data)?.DurationSeconds,
                "DSP" => WiiDspAudio.Probe(data)?.DurationSeconds,
                "WAV" => StandardAudioFormatSupport.ProbeWave(data)?.DurationSeconds,
                "WMA" => StandardAudioFormatSupport.ProbeWindowsMediaAudio(data)?.DurationSeconds,
                "DEE" => Thps4PcDeeAudio.Probe(data)?.DurationSeconds,
                "SMO" => Thps4PcSmoAudio.Probe(data)?.DurationSeconds,
                "PS3 MP3" => LatePlatformAudio.ProbeMpegLayer3(data)?.DurationSeconds,
                "PS3 FSB3" => LatePlatformAudio.ProbePs3Fsb3(data)?.DurationSeconds,
                "XMA1" => LatePlatformAudio.ProbeXma1(data)?.DurationSeconds,
                "SND" => Thug2PcSndDecoder.Probe(data)?.DurationSeconds,
                "PSS" => PssAudioExtractor.Probe(data)?.DurationSeconds,
                "PMF" => PsmfAudioExtractor.Probe(data) is { HasAudio: true } pmf
                    ? pmf.DurationSeconds
                    : null,
                "VID" => Vid1AudioExtractor.ProbeTracks(data)
                    .Select(static track => track.DurationSeconds)
                    .Where(static duration => duration is > 0)
                    .DefaultIfEmpty()
                    .Max(),
                "SEQ" => ProbeSeq(data),
                "VAB" or "KAT" or "FSB3" or "THAW XMA" => null,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Converts a decoded per-channel frame count to seconds.</summary>
    public static double? FromDecodedFrames(long? decodedFrameCount, int sampleRate)
    {
        return decodedFrameCount is > 0 && sampleRate > 0
            ? decodedFrameCount.Value / (double)sampleRate
            : null;
    }

    /// <summary>
    ///     SEQ duration from the tempo map — cheap (no synthesis): walk the
    ///     event ticks, converting through each tempo change.
    /// </summary>
    private static double? ProbeSeq(byte[] data)
    {
        var seq = SeqFile.Parse(data);
        if (seq == null || seq.Events.Count == 0)
            return null;

        var secondsPerTick = seq.InitialTempoMicroseconds / 1_000_000.0 / seq.Resolution;
        long lastTick = 0;
        var seconds = 0.0;
        foreach (var seqEvent in seq.Events)
        {
            seconds += (seqEvent.Tick - lastTick) * secondsPerTick;
            lastTick = seqEvent.Tick;
            if (seqEvent.Type == SeqEventType.Tempo && seqEvent.Value > 0)
                secondsPerTick = seqEvent.Value / 1_000_000.0 / seq.Resolution;
        }

        return seconds;
    }

    private static double? ProbeXa(byte[] data)
    {
        var channels = XaExtractor.EnumerateChannels(data);
        if (channels.Count > 0)
            return channels.Max(static channel => channel.DurationSeconds);

        if (data.Length < XaSoundGroupSize || data.Length % XaSoundGroupSize != 0)
            return null;

        var frames = (long)(data.Length / XaSoundGroupSize) * XaRawFramesPerGroup;
        return frames / (double)XaRawSampleRate;
    }
}
