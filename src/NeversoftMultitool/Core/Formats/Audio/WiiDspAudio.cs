using System.Buffers.Binary;
using NeversoftMultitool.Core.BinaryIO;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>Validated metadata for a standard Nintendo DSP-ADPCM stream.</summary>
public sealed record WiiDspAudioProbeResult(
    int SampleCount,
    int SampleRate,
    bool IsLooping,
    double DurationSeconds);

/// <summary>
///     Decodes the headered, mono Nintendo DSP-ADPCM streams used by the Wii
///     builds of Downhill Jam and Proving Ground. These files have no suffix;
///     routing must therefore be based on their complete 0x60-byte header and
///     payload-size identity rather than their names.
/// </summary>
public static class WiiDspAudio
{
    public const int HeaderSize = 0x60;
    private const int FrameSize = 8;
    private const int SamplesPerFrame = 14;
    private const int CoefficientOffset = 0x1C;
    private const int InitialPredictorScaleOffset = 0x3E;
    private const int InitialHistory1Offset = 0x40;
    private const int InitialHistory2Offset = 0x42;

    public static bool IsWiiDsp(ReadOnlySpan<byte> data) => Probe(data) != null;

    /// <summary>
    ///     Validates the whole stored stream without decoding it. In addition
    ///     to the standard header fields, the declared sample/nibble relation,
    ///     exact file length, initial predictor/scale byte and every frame's
    ///     predictor index must agree.
    /// </summary>
    public static WiiDspAudioProbeResult? Probe(ReadOnlySpan<byte> data)
    {
        if (!TryReadHeader(data, out var header))
            return null;

        var encoded = data[HeaderSize..];
        for (var offset = 0; offset < encoded.Length; offset += FrameSize)
        {
            if ((encoded[offset] >> 4) > 7)
                return null;
        }

        return new WiiDspAudioProbeResult(
            header.SampleCount,
            header.SampleRate,
            header.IsLooping,
            header.SampleCount / (double)header.SampleRate);
    }

    public static short[] Decode(ReadOnlySpan<byte> data)
    {
        if (!TryReadHeader(data, out var header))
            throw new InvalidDataException("Not a complete Nintendo DSP-ADPCM stream");

        var coefficients = new short[16];
        for (var index = 0; index < coefficients.Length; index++)
        {
            coefficients[index] = BinaryPrimitives.ReadInt16BigEndian(
                data.Slice(CoefficientOffset + index * sizeof(short), sizeof(short)));
        }

        var history1 = (int)BinaryPrimitives.ReadInt16BigEndian(
            data.Slice(InitialHistory1Offset, sizeof(short)));
        var history2 = (int)BinaryPrimitives.ReadInt16BigEndian(
            data.Slice(InitialHistory2Offset, sizeof(short)));
        var samples = new short[header.SampleCount];
        var sampleIndex = 0;
        var encoded = data[HeaderSize..];

        for (var frameOffset = 0;
             frameOffset < encoded.Length && sampleIndex < samples.Length;
             frameOffset += FrameSize)
        {
            var predictorScale = encoded[frameOffset];
            var predictor = predictorScale >> 4;
            if (predictor > 7)
                throw new InvalidDataException($"DSP frame {frameOffset / FrameSize} has predictor {predictor}");

            var scale = 1 << (predictorScale & 0x0F);
            var coefficient1 = coefficients[predictor * 2];
            var coefficient2 = coefficients[predictor * 2 + 1];

            for (var frameSample = 0;
                 frameSample < SamplesPerFrame && sampleIndex < samples.Length;
                 frameSample++)
            {
                var packed = encoded[frameOffset + 1 + frameSample / 2];
                var nibble = (frameSample & 1) == 0 ? packed >> 4 : packed & 0x0F;
                if (nibble >= 8)
                    nibble -= 16;

                var reconstructed = (((long)nibble * scale << 11)
                                     + 1024
                                     + (long)coefficient1 * history1
                                     + (long)coefficient2 * history2) >> 11;
                var sample = (short)Math.Clamp(reconstructed, short.MinValue, short.MaxValue);
                samples[sampleIndex++] = sample;
                history2 = history1;
                history1 = sample;
            }
        }

        return samples;
    }

    public static AudioConvertResult ConvertToWav(
        byte[] data,
        string outputStem,
        string outputDirectory)
    {
        var probe = Probe(data);
        if (probe == null)
        {
            return new AudioConvertResult
            {
                Skipped = true,
                ErrorMessage = "Not a complete Nintendo DSP-ADPCM stream"
            };
        }

        if (string.IsNullOrWhiteSpace(outputStem)
            || Path.IsPathRooted(outputStem)
            || !Path.GetFileName(outputStem).Equals(outputStem, StringComparison.Ordinal)
            || outputStem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return new AudioConvertResult
            {
                ErrorMessage = "Output stem must be a plain file-name stem."
            };
        }

        string? stagedPath = null;
        try
        {
            var samples = Decode(data);
            if (samples.Length == 0)
                return new AudioConvertResult { Skipped = true, ErrorMessage = "Stream carries no samples" };

            Directory.CreateDirectory(outputDirectory);
            var outputPath = Path.Combine(outputDirectory, outputStem + ".wav");
            stagedPath = Path.Combine(outputDirectory, $".{Guid.NewGuid():N}.wav");
            WavWriter.WritePcm16(stagedPath, probe.SampleRate, 1, samples);
            File.Move(stagedPath, outputPath, overwrite: true);
            stagedPath = null;
            // AudioConvertResult counts output WAV files, not decoded PCM frames.
            return new AudioConvertResult { Success = true, SamplesWritten = 1 };
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException
                                   or OverflowException or UnauthorizedAccessException)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
        finally
        {
            if (stagedPath != null)
            {
                try
                {
                    File.Delete(stagedPath);
                }
                catch (IOException)
                {
                    // Best-effort cleanup; conversion result already carries the primary failure.
                }
                catch (UnauthorizedAccessException)
                {
                    // Best-effort cleanup; conversion result already carries the primary failure.
                }
            }
        }
    }

    private static bool TryReadHeader(ReadOnlySpan<byte> data, out DspHeader header)
    {
        header = default;
        if (data.Length < HeaderSize + FrameSize)
            return false;

        var rawSampleCount = BinaryPrimitives.ReadUInt32BigEndian(data);
        var rawNibbleCount = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
        var rawSampleRate = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8, 4));
        var loopFlag = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(0x0C, 2));
        var format = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(0x0E, 2));
        if (rawSampleCount == 0
            || rawSampleCount > int.MaxValue
            || rawSampleRate is < 4_000 or > 192_000
            || loopFlag > 1
            || format != 0)
        {
            return false;
        }

        var sampleCount = (int)rawSampleCount;
        var frameCount = ((long)sampleCount + SamplesPerFrame - 1) / SamplesPerFrame;
        var expectedNibbleCount = frameCount * FrameSize * 2;
        var encodedByteCount = frameCount * FrameSize;
        if (rawNibbleCount != expectedNibbleCount
            || encodedByteCount > int.MaxValue
            || data.Length != HeaderSize + encodedByteCount)
        {
            return false;
        }

        var initialPredictorScale = BinaryPrimitives.ReadUInt16BigEndian(
            data.Slice(InitialPredictorScaleOffset, sizeof(ushort)));
        if (initialPredictorScale > byte.MaxValue
            || (byte)initialPredictorScale != data[HeaderSize])
        {
            return false;
        }

        header = new DspHeader(sampleCount, (int)rawSampleRate, loopFlag != 0);
        return true;
    }

    private readonly record struct DspHeader(int SampleCount, int SampleRate, bool IsLooping);
}
