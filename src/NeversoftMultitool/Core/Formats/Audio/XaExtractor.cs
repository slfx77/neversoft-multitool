using NeversoftMultitool.Core.BinaryIO;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Enumerates and extracts the interleaved channels of a sectored XA file.
///     A sectored stream multiplexes independent audio channels by the
///     sub-header channel byte; decoding reuses <see cref="XaDecoder" />.
/// </summary>
public static class XaExtractor
{
    private const int SectorSize = 2336;
    private const int AudioBytesPerSector = 18 * 128; // sound groups × group size

    // 18 sound groups × 8 units × 28 samples; stereo interleaves L/R, halving frames.
    private const int SamplesPerSector = 18 * 8 * 28;

    /// <summary>
    ///     Lists the channels present in a sectored XA file, with per-channel
    ///     format (from each channel's first sector coding byte) and a duration
    ///     estimated from its sector count. Returns an empty list for
    ///     non-sectored (raw) XA data, which is single-stream by definition.
    /// </summary>
    public static List<XaChannelInfo> EnumerateChannels(byte[] data)
    {
        var results = new List<XaChannelInfo>();
        if (!XaDecoder.IsSectored(data))
            return results;

        var sectorCount = data.Length / SectorSize;
        var sectorsByChannel = new Dictionary<int, int>();
        var codingByChannel = new Dictionary<int, byte>();

        for (var s = 0; s < sectorCount; s++)
        {
            var offset = s * SectorSize;
            var channel = data[offset + 1];
            if (sectorsByChannel.TryAdd(channel, 1))
                codingByChannel[channel] = data[offset + 3];
            else
                sectorsByChannel[channel]++;
        }

        foreach (var channel in sectorsByChannel.Keys.OrderBy(c => c))
        {
            var coding = codingByChannel[channel];
            var isStereo = (coding & 0x01) != 0;
            var sampleRate = (coding & 0x04) != 0 ? 18900 : 37800;
            var sectors = sectorsByChannel[channel];
            var frames = (long)sectors * SamplesPerSector / (isStereo ? 2 : 1);

            results.Add(new XaChannelInfo(
                channel,
                sectors,
                sampleRate,
                isStereo,
                (double)frames / sampleRate,
                sectors * AudioBytesPerSector));
        }

        return results;
    }

    /// <summary>
    ///     Decodes a single channel to <c>{stem}_chNN.wav</c> in
    ///     <paramref name="outputDir" />. Returns the output path, or null when
    ///     the data is not sectored XA or the channel is absent.
    /// </summary>
    public static string? ExtractChannelToWav(byte[] data, string stem, int channelNumber, string outputDir)
    {
        var decoded = XaDecoder.DecodeToSamples(data, channelNumber);
        if (decoded == null || decoded.Value.Samples.Length == 0)
            return null;

        var (samples, sampleRate, channels) = decoded.Value;
        var wavPath = Path.Combine(outputDir, $"{stem}_ch{channelNumber:D2}.wav");
        WavWriter.WritePcm16(wavPath, sampleRate, channels, samples);
        return wavPath;
    }

    public sealed record XaChannelInfo(
        int ChannelNumber,
        int SectorCount,
        int SampleRate,
        bool IsStereo,
        double DurationSeconds,
        int DataSize);
}
