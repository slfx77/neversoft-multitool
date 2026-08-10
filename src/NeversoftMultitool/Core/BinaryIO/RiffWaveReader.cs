using System.Buffers.Binary;

namespace NeversoftMultitool.Core.BinaryIO;

/// <summary>
///     The parsed <c>fmt </c>/<c>data</c> pair of a RIFF/WAVE container.
/// </summary>
/// <param name="FormatTag">wFormatTag: 1 = PCM, 0x0069 = Xbox ADPCM.</param>
/// <param name="AvgBytesPerSec">
///     nAvgBytesPerSec as stored. NOT trustworthy as a byte rate — THUG2 PC
///     <c>.snd</c> files repurpose this field to carry the DECODED byte count.
/// </param>
/// <param name="SamplesPerBlock">wSamplesPerBlock, or 0 when the fmt chunk is shorter than 20 bytes.</param>
public readonly record struct RiffWaveInfo(
    int FormatTag,
    int Channels,
    int SampleRate,
    int AvgBytesPerSec,
    int BlockAlign,
    int BitsPerSample,
    int SamplesPerBlock,
    int DataOffset,
    int DataLength);

/// <summary>
///     Minimal RIFF/WAVE chunk walker, written against the real Neversoft corpus
///     rather than the spec, because the shipped files break the spec in two ways
///     that a naive reader trips over:
///     <list type="bullet">
///         <item>
///             The RIFF size field is unreliable — all 788 THUG2 PC <c>.snd</c>
///             declare roughly 4x the actual file length, and 2,660 of 2,752
///             <c>.pcm</c> are off by 2. The walk is bounded by the real buffer.
///         </item>
///         <item>
///             Around 130 <c>.snd</c> carry corrupt trailing chunks (ids like
///             <c>mpl&lt;</c>, <c>extZ</c>) from their authoring tool, so the walk
///             STOPS at <c>data</c> and never continues past it.
///         </item>
///     </list>
///     Payload offsets reach 1,028 bytes in the combined THUG2 audio corpus
///     (an Xbox <c>.pcm</c> with a <c>bext</c>-prefixed broadcast-WAV layout;
///     PC <c>.snd</c> reaches 1,024), so a header prefix read must be generous.
/// </summary>
public static class RiffWaveReader
{
    /// <summary>Bytes to read when probing a file: the largest observed PCM/SND data offset is 1,028.</summary>
    public const int HeaderProbeBytes = 8192;

    public static bool IsRiffWave(ReadOnlySpan<byte> data)
    {
        return data.Length >= 12
               && data[..4].SequenceEqual("RIFF"u8)
               && data.Slice(8, 4).SequenceEqual("WAVE"u8);
    }

    public static bool TryRead(ReadOnlySpan<byte> data, out RiffWaveInfo info)
    {
        info = default;
        if (!IsRiffWave(data))
            return false;

        var haveFormat = false;
        int formatTag = 0, channels = 0, sampleRate = 0, avgBytesPerSec = 0;
        int blockAlign = 0, bitsPerSample = 0, samplesPerBlock = 0;

        var offset = 12;
        while (offset + 8 <= data.Length)
        {
            var id = data.Slice(offset, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4));
            var payload = offset + 8;

            if (id.SequenceEqual("fmt "u8))
            {
                if (size < 16 || payload + 16 > data.Length)
                    return false;

                formatTag = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(payload, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(payload + 2, 2));
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(payload + 4, 4));
                avgBytesPerSec = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(payload + 8, 4));
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(payload + 12, 2));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(payload + 14, 2));

                // wSamplesPerBlock lives at fmt+18, present only when the extension
                // (cbSize) is at least 2 bytes.
                if (size >= 20 && payload + 20 <= data.Length)
                    samplesPerBlock = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(payload + 18, 2));

                haveFormat = true;
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (!haveFormat)
                    return false;

                // Clamp: the declared size is not trustworthy (see class remarks).
                var available = data.Length - payload;
                var length = size > (uint)available ? available : (int)size;
                info = new RiffWaveInfo(
                    formatTag, channels, sampleRate, avgBytesPerSec,
                    blockAlign, bitsPerSample, samplesPerBlock, payload, length);
                return true;
            }

            // Advance with the RIFF word-pad, guarding against a bogus size that
            // would overflow or walk backwards.
            var advance = 8L + size + (size & 1);
            if (advance <= 0 || offset + advance > data.Length)
                return false;
            offset += (int)advance;
        }

        return false;
    }

    /// <summary>
    ///     Reads a bounded prefix of a file and parses it. Enough for every
    ///     corpus layout; a file whose <c>data</c> chunk starts beyond
    ///     <see cref="HeaderProbeBytes" /> simply fails to probe.
    /// </summary>
    public static bool TryReadHeader(string path, out RiffWaveInfo info)
    {
        info = default;
        try
        {
            using var stream = File.OpenRead(path);
            var length = (int)Math.Min(HeaderProbeBytes, stream.Length);
            var buffer = new byte[length];
            stream.ReadExactly(buffer, 0, length);
            return TryRead(buffer, out info);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
