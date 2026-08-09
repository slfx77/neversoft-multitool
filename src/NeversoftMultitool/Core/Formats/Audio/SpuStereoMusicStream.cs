namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Detection + decode for Neversoft PS2 music streams (THPS3/THPS4/THUG-era
///     MUSIC.WAD payloads): headerless SPU-ADPCM where stereo ships as
///     alternating 0x18000-byte L/R chunks played at 48 kHz.
///     Ground truth is the THUG source (Gel/Music/Ngps): the EE reads
///     MUSIC_IOP_BUFFER_SIZE (192K) sequentially (p_music.cpp:336-340) and the
///     IOP feeds the L voice from the buffer's first half and the R voice from
///     the second (pcm.h MUSIC_L/R_IOP_OFFSET, pcm_com.c DownloadMusic), at
///     DEFAULT_PITCH 0x1000 = 48000 Hz (pcm.h:14). Voice streams play mono at
///     STREAM_PITCH = 22050 Hz (pcm_com.c:19) — the existing headerless default.
///     Empirically confirmed: channel-envelope correlation spikes to 0.86-0.96
///     at 0x18000 on THPS3/THPS4/THUG music vs ~0.1 at every other chunk size
///     in the corpus comparison.
/// </summary>
public static class SpuStereoMusicStream
{
    /// <summary>MUSIC_HALF_IOP_BUFFER_SIZE — one channel's chunk in the stream.</summary>
    public const int ChunkSize = 0x18000;

    /// <summary>DEFAULT_PITCH 0x1000 on SPU2 = native 48 kHz.</summary>
    public const int SampleRate = 48000;

    private const int MinPairs = 2;
    private const int DetectionPairBudget = 24;
    private const int EnvelopeWindow = 1024;
    private const double CorrelationThreshold = 0.55;

    /// <summary>
    ///     True when the headerless stream reads as chunk-interleaved stereo:
    ///     the two de-interleaved channels' loudness envelopes correlate far
    ///     above anything a mono stream produces at this chunk size (a mono
    ///     file's pseudo-channels are ~7.8 s-offset segment collages).
    /// </summary>
    public static bool IsStereoMusic(ReadOnlySpan<byte> data)
    {
        if (data.Length < ChunkSize * 2 * MinPairs)
            return false;

        var pairs = Math.Min(data.Length / (ChunkSize * 2), DetectionPairBudget);
        var (left, right) = SplitChannels(data[..(pairs * ChunkSize * 2)]);

        var leftEnvelope = Envelope(SpuAdpcm.Decode(left));
        var rightEnvelope = Envelope(SpuAdpcm.Decode(right));

        return Pearson(leftEnvelope, rightEnvelope) >= CorrelationThreshold;
    }

    /// <summary>Decodes the full stream to interleaved L/R 16-bit PCM.</summary>
    public static short[] DecodeInterleaved(ReadOnlySpan<byte> data)
    {
        var (left, right) = SplitChannels(data);
        var leftPcm = SpuAdpcm.Decode(left);
        var rightPcm = SpuAdpcm.Decode(right);

        // The final file chunk can be L-only (the song ends mid-pair) —
        // truncate to the shorter channel rather than emitting a lopsided tail.
        var frames = Math.Min(leftPcm.Length, rightPcm.Length);
        var pcm = new short[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            pcm[i * 2] = leftPcm[i];
            pcm[i * 2 + 1] = rightPcm[i];
        }

        return pcm;
    }

    /// <summary>Estimated duration in seconds (per-channel samples at 48 kHz).</summary>
    public static double EstimateDuration(long fileLength)
    {
        var samplesPerChannel = fileLength / 2 / SpuAdpcm.BlockSize * SpuAdpcm.SamplesPerBlock;
        return samplesPerChannel / (double)SampleRate;
    }

    private static (byte[] Left, byte[] Right) SplitChannels(ReadOnlySpan<byte> data)
    {
        var pairSize = ChunkSize * 2;
        var fullPairs = data.Length / pairSize;
        var remainder = data.Length - fullPairs * pairSize;
        var leftTail = Math.Min(remainder, ChunkSize);
        var rightTail = remainder - leftTail;

        var left = new byte[fullPairs * ChunkSize + leftTail];
        var right = new byte[fullPairs * ChunkSize + rightTail];

        for (var i = 0; i < fullPairs; i++)
        {
            data.Slice(i * pairSize, ChunkSize).CopyTo(left.AsSpan(i * ChunkSize));
            data.Slice(i * pairSize + ChunkSize, ChunkSize).CopyTo(right.AsSpan(i * ChunkSize));
        }

        if (leftTail > 0)
            data.Slice(fullPairs * pairSize, leftTail).CopyTo(left.AsSpan(fullPairs * ChunkSize));
        if (rightTail > 0)
            data.Slice(fullPairs * pairSize + ChunkSize, rightTail).CopyTo(right.AsSpan(fullPairs * ChunkSize));

        return (left, right);
    }

    private static double[] Envelope(short[] samples)
    {
        var windows = samples.Length / EnvelopeWindow;
        var envelope = new double[windows];
        for (var w = 0; w < windows; w++)
        {
            long sum = 0;
            var start = w * EnvelopeWindow;
            for (var i = 0; i < EnvelopeWindow; i++)
                sum += Math.Abs((int)samples[start + i]);
            envelope[w] = sum / (double)EnvelopeWindow;
        }

        return envelope;
    }

    private static double Pearson(double[] a, double[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        if (n < 8)
            return 0.0;

        double meanA = 0, meanB = 0;
        for (var i = 0; i < n; i++)
        {
            meanA += a[i];
            meanB += b[i];
        }

        meanA /= n;
        meanB /= n;

        double cov = 0, varA = 0, varB = 0;
        for (var i = 0; i < n; i++)
        {
            var da = a[i] - meanA;
            var db = b[i] - meanB;
            cov += da * db;
            varA += da * da;
            varB += db * db;
        }

        return varA > 0 && varB > 0 ? cov / Math.Sqrt(varA * varB) : 0.0;
    }
}
