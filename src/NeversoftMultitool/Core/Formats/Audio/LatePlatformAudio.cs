using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Audio;

public enum LatePlatformAudioKind
{
    Ps3MpegLayer3,
    Ps3Fsb3,
    Xbox360Xma1
}

/// <summary>Metadata for a late-generation compound-suffix audio asset.</summary>
public sealed record LatePlatformAudioProbeResult(
    LatePlatformAudioKind Kind,
    int? SampleRate,
    int? Channels,
    double? DurationSeconds,
    int FrameOrPacketCount,
    long? TimelineSampleCount,
    int? LoopCount = null,
    string? CodecName = null);

/// <summary>
///     Strict content gate and WAV conversion for Project 8 / Proving Ground
///     <c>.wav.ps3</c> and <c>.wav.xen</c> assets. The former is either one raw
///     MPEG-Layer-III stream or a complete FSB3.1 bank; the latter is the
///     game's exact legacy RIFF/XMA1-with-seek-table dialect.
/// </summary>
public static class LatePlatformAudio
{
    private const string Ps3Suffix = ".wav.ps3";
    private const string XenSuffix = ".wav.xen";
    private const int XmaHeaderSize = 60;
    private const int XmaPacketSize = 2048;

    public static bool HasSupportedFileName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var name = Path.GetFileName(path);
        return name.EndsWith(Ps3Suffix, StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(XenSuffix, StringComparison.OrdinalIgnoreCase);
    }

    public static LatePlatformAudioProbeResult? Probe(string inputPath)
    {
        try
        {
            return Probe(inputPath, File.ReadAllBytes(inputPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static LatePlatformAudioProbeResult? Probe(string fileName, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var leaf = Path.GetFileName(fileName);
        if (leaf.EndsWith(Ps3Suffix, StringComparison.OrdinalIgnoreCase))
        {
            return data.AsSpan().StartsWith("FSB3"u8)
                ? ProbePs3Fsb3(data)
                : ProbeMpegLayer3(data);
        }

        return leaf.EndsWith(XenSuffix, StringComparison.OrdinalIgnoreCase)
            ? ProbeXma1(data)
            : null;
    }

    public static AudioConvertResult ConvertToWav(
        string inputPath,
        string outputStem,
        string outputDirectory)
    {
        try
        {
            var probe = Probe(inputPath);
            if (probe == null)
                return NotThisFormat();

            // FFmpeg's FSB demuxer does not implement this corpus' FSB3 MP3
            // subtype. Route banks through the byte path so that subtype can
            // be unwrapped without weakening the FSB content gate.
            if (probe.Kind == LatePlatformAudioKind.Ps3Fsb3)
            {
                return ConvertToWav(
                    File.ReadAllBytes(inputPath),
                    Path.GetFileName(inputPath),
                    outputStem,
                    outputDirectory,
                    null);
            }

            return StrictFfmpegAudioConverter.ConvertPath(
                inputPath,
                outputStem,
                outputDirectory,
                probe.SampleRate!.Value,
                probe.Channels!.Value,
                GetLayoutName(probe.Kind));
        }
        catch (Exception ex)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
    }

    public static AudioConvertResult ConvertToWav(
        byte[] data,
        string fileName,
        string outputStem,
        string outputDirectory)
    {
        return ConvertToWav(data, fileName, outputStem, outputDirectory, null);
    }

    internal static AudioConvertResult ConvertToWav(
        byte[] data,
        string fileName,
        string outputStem,
        string outputDirectory,
        AudioPcmTranscoder? transcoder)
    {
        ArgumentNullException.ThrowIfNull(data);
        var probe = Probe(fileName, data);
        if (probe == null)
            return NotThisFormat();

        var stagedData = data;
        var stagedExtension = probe.Kind switch
        {
            LatePlatformAudioKind.Ps3MpegLayer3 => ".mp3",
            LatePlatformAudioKind.Ps3Fsb3 => ".fsb",
            _ => ".xma"
        };
        if (probe.Kind == LatePlatformAudioKind.Ps3Fsb3
            && probe.CodecName == "MPEG Layer III")
        {
            stagedData = ExtractFsbMpegPayload(data);
            stagedExtension = ".mp3";
        }

        return StrictFfmpegAudioConverter.ConvertBytes(
            stagedData,
            stagedExtension,
            outputStem,
            outputDirectory,
            probe.SampleRate!.Value,
            probe.Channels!.Value,
            GetLayoutName(probe.Kind),
            transcoder);
    }

    internal static LatePlatformAudioProbeResult? ProbeMpegLayer3(ReadOnlySpan<byte> data)
    {
        return ProbeMpegLayer3(data, allowFsbAlignmentPadding: false, out _);
    }

    private static LatePlatformAudioProbeResult? ProbeMpegLayer3(
        ReadOnlySpan<byte> data,
        bool allowFsbAlignmentPadding,
        out int contentLength)
    {
        contentLength = 0;
        var position = 0;
        var frameCount = 0;
        var sampleRate = 0;
        var channels = 0;
        var version = -1;
        var samplesPerFrame = 0;

        while (position < data.Length)
        {
            var remaining = data.Length - position;
            if (allowFsbAlignmentPadding
                && data.Length % 16 == 0
                && remaining <= 15
                && data[position..].IndexOfAnyExcept((byte)0) < 0)
            {
                contentLength = position;
                position = data.Length;
                break;
            }

            if (data.Length - position < sizeof(uint))
                return null;

            var header = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
            if ((header & 0xffe00000U) != 0xffe00000U)
                return null;

            var currentVersion = (int)((header >> 19) & 0x3);
            var layer = (int)((header >> 17) & 0x3);
            var bitrateIndex = (int)((header >> 12) & 0xf);
            var sampleRateIndex = (int)((header >> 10) & 0x3);
            var padding = (int)((header >> 9) & 0x1);
            var channelMode = (int)((header >> 6) & 0x3);
            var emphasis = (int)(header & 0x3);
            if (currentVersion == 1
                || layer != 1
                || bitrateIndex is 0 or 15
                || sampleRateIndex == 3
                || emphasis == 2)
            {
                return null;
            }

            var currentSampleRate = GetMpegSampleRate(currentVersion, sampleRateIndex);
            var currentChannels = channelMode == 3 ? 1 : 2;
            var bitrate = GetLayer3Bitrate(currentVersion, bitrateIndex);
            var currentSamplesPerFrame = currentVersion == 3 ? 1152 : 576;
            var coefficient = currentVersion == 3 ? 144 : 72;
            var frameLength = checked(coefficient * bitrate * 1000 / currentSampleRate + padding);
            if (frameLength < 4 || frameLength > data.Length - position)
                return null;

            if (frameCount == 0)
            {
                version = currentVersion;
                sampleRate = currentSampleRate;
                channels = currentChannels;
                samplesPerFrame = currentSamplesPerFrame;
            }
            else if (currentVersion != version
                     || currentSampleRate != sampleRate
                     || currentChannels != channels)
            {
                return null;
            }

            position += frameLength;
            frameCount++;
        }

        // The corpus minimum is three complete frames. Requiring that measured
        // floor avoids claiming short sync-like data as a raw MP3 stream.
        if (position != data.Length || frameCount < 3)
            return null;

        if (contentLength == 0)
            contentLength = data.Length;

        var encodedSamples = checked((long)frameCount * samplesPerFrame);
        return new LatePlatformAudioProbeResult(
            LatePlatformAudioKind.Ps3MpegLayer3,
            sampleRate,
            channels,
            encodedSamples / (double)sampleRate,
            frameCount,
            encodedSamples,
            null,
            "MPEG Layer III");
    }

    internal static LatePlatformAudioProbeResult? ProbePs3Fsb3(ReadOnlySpan<byte> data)
    {
        const int mainHeaderSize = 24;
        const int minimumSampleHeaderSize = 80;
        if (data.Length < mainHeaderSize + minimumSampleHeaderSize + 4
            || !data[..4].SequenceEqual("FSB3"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(data[4..8]) != 1
            || BinaryPrimitives.ReadUInt32LittleEndian(data[16..20]) != 0x00030001
            || BinaryPrimitives.ReadUInt32LittleEndian(data[20..24]) != 0)
        {
            return null;
        }

        var headerSectionSize = BinaryPrimitives.ReadUInt32LittleEndian(data[8..12]);
        var encodedSize = BinaryPrimitives.ReadUInt32LittleEndian(data[12..16]);
        if (headerSectionSize is not 80 and not 88
            || encodedSize == 0
            || mainHeaderSize + (long)headerSectionSize + encodedSize != data.Length)
        {
            return null;
        }

        var header = data.Slice(mainHeaderSize, checked((int)headerSectionSize));
        // FSB3 stores the 80-byte base sample-header size here; codec-specific
        // extension bytes are counted only by the bank's aggregate header size.
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[..2]) != minimumSampleHeaderSize
            || !HasSafeFsbName(header[2..32]))
        {
            return null;
        }

        var decodedSamples = BinaryPrimitives.ReadUInt32LittleEndian(header[32..36]);
        var sampleEncodedSize = BinaryPrimitives.ReadUInt32LittleEndian(header[36..40]);
        var loopStart = BinaryPrimitives.ReadUInt32LittleEndian(header[40..44]);
        var loopEnd = BinaryPrimitives.ReadUInt32LittleEndian(header[44..48]);
        var mode = BinaryPrimitives.ReadUInt32LittleEndian(header[48..52]);
        var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(header[52..56]);
        var channels = BinaryPrimitives.ReadUInt16LittleEndian(header[62..64]);
        if (decodedSamples == 0
            || sampleEncodedSize != encodedSize
            || loopStart > loopEnd
            || loopEnd >= decodedSamples
            || sampleRate is not 22_050 and not 24_000 and not 44_100 and not 48_000
            || channels is not 1 and not 2
            || BinaryPrimitives.ReadUInt16LittleEndian(header[56..58]) != 255
            || BinaryPrimitives.ReadInt16LittleEndian(header[58..60]) != 128
            || BinaryPrimitives.ReadUInt16LittleEndian(header[60..62]) != 255
            || BinaryPrimitives.ReadSingleLittleEndian(header[64..68]) != 1f
            || BinaryPrimitives.ReadSingleLittleEndian(header[68..72]) != 10_000f
            || BinaryPrimitives.ReadInt32LittleEndian(header[72..76]) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(header[76..78]) != 0
            || BinaryPrimitives.ReadInt16LittleEndian(header[78..80]) != 0)
        {
            return null;
        }

        var payload = data[(mainHeaderSize + (int)headerSectionSize)..];
        string codecName;
        int unitCount;
        if (mode is 0x00000220 or 0x00000240)
        {
            if (headerSectionSize != 88
                || header[80..88].IndexOfAnyExcept((byte)0) >= 0)
            {
                return null;
            }

            var mp3 = ProbeMpegLayer3(
                payload,
                allowFsbAlignmentPadding: true,
                out _);
            if (mp3 == null
                || mp3.SampleRate != sampleRate
                || mp3.Channels != channels
                || channels != (mode == 0x00000240 ? 2 : 1))
            {
                return null;
            }

            codecName = "MPEG Layer III";
            unitCount = mp3.FrameOrPacketCount;
        }
        else if (mode is 0x00400020 or 0x00400040)
        {
            if (headerSectionSize != 80
                || channels != (mode == 0x00400040 ? 2 : 1))
            {
                return null;
            }

            var blockSize = checked(36 * channels);
            var blockCount = checked(((long)decodedSamples + 63) / 64);
            if (blockCount * blockSize != encodedSize)
                return null;

            for (var blockOffset = 0; blockOffset < payload.Length; blockOffset += blockSize)
            {
                for (var channel = 0; channel < channels; channel++)
                {
                    var stateOffset = blockOffset + channel * 4;
                    if (payload[stateOffset + 2] > 88 || payload[stateOffset + 3] != 0)
                        return null;
                }
            }

            codecName = "IMA ADPCM";
            unitCount = checked((int)blockCount);
        }
        else
        {
            return null;
        }

        return new LatePlatformAudioProbeResult(
            LatePlatformAudioKind.Ps3Fsb3,
            sampleRate,
            channels,
            decodedSamples / (double)sampleRate,
            unitCount,
            decodedSamples,
            null,
            codecName);
    }

    internal static LatePlatformAudioProbeResult? ProbeXma1(ReadOnlySpan<byte> data)
    {
        if (data.Length < XmaHeaderSize + XmaPacketSize + 12
            || !data[..4].SequenceEqual("RIFF"u8)
            || (long)BinaryPrimitives.ReadUInt32LittleEndian(data[4..8]) + 8 != data.Length
            || !data[8..12].SequenceEqual("WAVE"u8)
            || !data[12..16].SequenceEqual("fmt "u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(data[16..20]) != 32
            || BinaryPrimitives.ReadUInt16LittleEndian(data[20..22]) != 0x0165
            || BinaryPrimitives.ReadUInt16LittleEndian(data[22..24]) != 16
            || BinaryPrimitives.ReadUInt16LittleEndian(data[24..26]) != 0x10d6
            || BinaryPrimitives.ReadUInt16LittleEndian(data[26..28]) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(data[28..30]) != 1
            || data[31] != 2)
        {
            return null;
        }

        var loopCount = data[30];
        var pseudoBytesPerSecond = BinaryPrimitives.ReadUInt32LittleEndian(data[32..36]);
        var sampleRate32 = BinaryPrimitives.ReadUInt32LittleEndian(data[36..40]);
        var loopStart = BinaryPrimitives.ReadUInt32LittleEndian(data[40..44]);
        var loopEnd = BinaryPrimitives.ReadUInt32LittleEndian(data[44..48]);
        var subframeData = data[48];
        var channels = data[49];
        var channelMask = BinaryPrimitives.ReadUInt16LittleEndian(data[50..52]);
        if (loopCount is not 0 and not 255
            || pseudoBytesPerSecond == 0
            || sampleRate32 is not 22_050 and not 44_100 and not 48_000
            || channels is not 1 and not 2
            || channelMask != 0
            || loopStart > loopEnd
            || (loopCount == 0 && (loopStart != 0 || loopEnd != 0 || subframeData != 0))
            || (subframeData >> 4) > 3
            || (subframeData & 0xf) > 4)
        {
            return null;
        }

        if (!data[52..56].SequenceEqual("data"u8))
            return null;

        var encodedSize = BinaryPrimitives.ReadUInt32LittleEndian(data[56..60]);
        if (encodedSize == 0 || encodedSize % XmaPacketSize != 0)
            return null;

        var dataEnd = XmaHeaderSize + (long)encodedSize;
        if (dataEnd + 12 > data.Length || dataEnd > int.MaxValue)
            return null;

        var packetCount = checked((int)(encodedSize / XmaPacketSize));
        var seekOffset = (int)dataEnd;
        if (!data.Slice(seekOffset, 4).SequenceEqual("seek"u8))
            return null;

        var seekSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(seekOffset + 4, 4));
        var expectedSeekSize = checked((packetCount + 2) * sizeof(uint));
        if (seekSize != expectedSeekSize
            || dataEnd + 8 + seekSize != data.Length
            || BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(seekOffset + 8, 4)) != 1
            || BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(seekOffset + 12, 4)) != packetCount)
        {
            return null;
        }

        uint previousSeek = 0;
        for (var packet = 0; packet < packetCount; packet++)
        {
            var packetOffset = XmaHeaderSize + packet * XmaPacketSize;
            if ((data[packetOffset + 2] & 0x07) != 0 || data[packetOffset + 3] != 0)
                return null;

            var seekValue = BinaryPrimitives.ReadUInt32LittleEndian(
                data.Slice(seekOffset + 16 + packet * sizeof(uint), sizeof(uint)));
            if (seekValue % 512 != 0
                || packet == 0 && seekValue != 0
                || packet > 0 && seekValue < previousSeek)
            {
                return null;
            }

            previousSeek = seekValue;
        }

        // XMA1 stores cumulative positions at packet starts but no terminal
        // decoded-sample count, so exact duration is intentionally unavailable.
        return new LatePlatformAudioProbeResult(
            LatePlatformAudioKind.Xbox360Xma1,
            checked((int)sampleRate32),
            channels,
            null,
            packetCount,
            null,
            loopCount,
            "XMA1");
    }

    public static string GetSourceStem(string path)
    {
        var leaf = Path.GetFileName(path);
        if (leaf.EndsWith(Ps3Suffix, StringComparison.OrdinalIgnoreCase)
            || leaf.EndsWith(XenSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return leaf[..^Ps3Suffix.Length];
        }

        return Path.GetFileNameWithoutExtension(leaf);
    }

    private static int GetMpegSampleRate(int version, int index)
    {
        var baseRate = index switch
        {
            0 => 44_100,
            1 => 48_000,
            2 => 32_000,
            _ => throw new InvalidDataException("Reserved MPEG sample-rate index")
        };
        return version switch
        {
            3 => baseRate,
            2 => baseRate / 2,
            0 => baseRate / 4,
            _ => throw new InvalidDataException("Reserved MPEG version")
        };
    }

    private static int GetLayer3Bitrate(int version, int index)
    {
        ReadOnlySpan<int> mpeg1 =
            [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
        ReadOnlySpan<int> mpeg2 =
            [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
        return version == 3 ? mpeg1[index] : mpeg2[index];
    }

    private static string GetLayoutName(LatePlatformAudioKind kind)
    {
        return kind switch
        {
            LatePlatformAudioKind.Ps3MpegLayer3 => "PS3 MP3",
            LatePlatformAudioKind.Ps3Fsb3 => "PS3 FSB3",
            _ => "Xbox 360 XMA1"
        };
    }

    private static byte[] ExtractFsbMpegPayload(ReadOnlySpan<byte> data)
    {
        var headerSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[8..12]));
        var payload = data[(24 + headerSize)..];
        if (ProbeMpegLayer3(
                payload,
                allowFsbAlignmentPadding: true,
                out var contentLength) == null)
        {
            throw new InvalidDataException("Invalid FSB3 MPEG payload");
        }

        return payload[..contentLength].ToArray();
    }

    private static bool HasSafeFsbName(ReadOnlySpan<byte> bytes)
    {
        var nullIndex = bytes.IndexOf((byte)0);
        var name = nullIndex >= 0 ? bytes[..nullIndex] : bytes;
        return !name.IsEmpty
               && name.IndexOfAnyInRange((byte)0, (byte)31) < 0;
    }

    private static AudioConvertResult NotThisFormat()
    {
        return new AudioConvertResult
        {
            Skipped = true,
            ErrorMessage = "Not an exact supported .wav.ps3/.wav.xen audio payload"
        };
    }
}
