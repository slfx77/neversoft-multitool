using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Losslessly demuxes the ATRAC3+ private stream carried by PSP PSMF/PMF
///     movies and wraps it as Sony OMA for ffmpeg. This class does not decode
///     ATRAC: ffmpeg remains the codec implementation.
/// </summary>
public static class PsmfAudioExtractor
{
    private const int PsmfHeaderMinimumSize = 16;
    private const int OmaHeaderSize = 96;
    private const int AtracFrameHeaderSize = 8;
    private const int SamplesPerFrame = 2048;
    private const byte PackHeaderId = 0xBA;
    private const byte ProgramEndId = 0xB9;
    private const byte PrivateStream1Id = 0xBD;
    private const int StartCodePrefix = 0x000001;

    // This is the sample-rate table used by FFmpeg's OMA demuxer, in hertz.
    private static readonly int[] SampleRates = [32_000, 44_100, 48_000, 88_200, 96_000];

    // ATRAC-X stores a channel-layout id rather than a literal channel count.
    // These counts mirror FFmpeg's oma_chid_to_native_layout table.
    private static readonly int[] ChannelCounts = [1, 2, 3, 4, 6, 7, 8];

    public static PsmfAudioProbeResult? Probe(string inputPath)
    {
        try
        {
            using var stream = File.OpenRead(inputPath);
            return TryRead(stream, out var audio, out _) ? audio.Metadata : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>In-memory counterpart used for archive-backed PMF entries.</summary>
    public static PsmfAudioProbeResult? Probe(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var stream = new MemoryStream(data, writable: false);
        return TryRead(stream, out var audio, out _) ? audio.Metadata : null;
    }

    /// <summary>Extracts a PMF's ATRAC3+ soundtrack as PCM16 WAV.</summary>
    public static AudioConvertResult ConvertToWav(string inputPath, string outputDirectory)
    {
        return ConvertToWav(
            inputPath,
            Path.GetFileNameWithoutExtension(inputPath),
            outputDirectory);
    }

    /// <summary>Path-backed conversion with an explicit collision-safe output stem.</summary>
    public static AudioConvertResult ConvertToWav(
        string inputPath,
        string outputStem,
        string outputDirectory)
    {
        return ConvertToWav(
            inputPath,
            null,
            outputStem,
            outputDirectory,
            null);
    }

    /// <summary>Archive-backed conversion from PMF bytes.</summary>
    public static AudioConvertResult ConvertToWav(
        byte[] data,
        string outputStem,
        string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(data);
        return ConvertToWav(
            null,
            data,
            outputStem,
            outputDirectory,
            null);
    }

    internal static AudioConvertResult ConvertToWav(
        string inputPath,
        string outputStem,
        string outputDirectory,
        AudioPcmTranscoder transcoder)
    {
        ArgumentNullException.ThrowIfNull(transcoder);
        return ConvertToWav(
            inputPath,
            null,
            outputStem,
            outputDirectory,
            transcoder);
    }

    internal static AudioConvertResult ConvertToWav(
        byte[] data,
        string outputStem,
        string outputDirectory,
        AudioPcmTranscoder transcoder)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(transcoder);
        return ConvertToWav(
            null,
            data,
            outputStem,
            outputDirectory,
            transcoder);
    }

    private static AudioConvertResult ConvertToWav(
        string? inputPath,
        byte[]? inputData,
        string outputStem,
        string outputDirectory,
        AudioPcmTranscoder? transcoder)
    {
        var omaPath = Path.Combine(
            Path.GetTempPath(),
            "NeversoftMultitool",
            "PsmfAudio",
            $"{Guid.NewGuid():N}.oma");

        try
        {
            var extracted = inputPath != null
                ? TryWriteOma(inputPath, omaPath, out var probe, out var error)
                : TryWriteOma(inputData!, omaPath, out probe, out error);
            if (!extracted)
                return new AudioConvertResult { ErrorMessage = error };

            if (!probe.HasAudio)
            {
                return new AudioConvertResult
                {
                    Skipped = true,
                    ErrorMessage = "PSMF contains no ATRAC3+ audio stream"
                };
            }

            return StrictFfmpegAudioConverter.ConvertPath(
                omaPath,
                outputStem,
                outputDirectory,
                probe.SampleRate,
                probe.Channels,
                "PSMF ATRAC3+",
                transcoder ?? StrictFfmpegAudioConverter.RunFfmpegWithXError);
        }
        catch (Exception ex)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
        finally
        {
            TryDeleteFile(omaPath);
        }
    }

    internal static bool TryWriteOma(
        string inputPath,
        string outputPath,
        out PsmfAudioProbeResult probe,
        out string error)
    {
        try
        {
            using var stream = File.OpenRead(inputPath);
            return TryWriteOma(stream, outputPath, out probe, out error);
        }
        catch (Exception ex)
        {
            probe = PsmfAudioProbeResult.Empty;
            error = ex.Message;
            return false;
        }
    }

    internal static bool TryWriteOma(
        byte[] data,
        string outputPath,
        out PsmfAudioProbeResult probe,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var stream = new MemoryStream(data, writable: false);
        return TryWriteOma(stream, outputPath, out probe, out error);
    }

    private static bool TryWriteOma(
        Stream stream,
        string outputPath,
        out PsmfAudioProbeResult probe,
        out string error)
    {
        try
        {
            if (!TryRead(stream, out var audio, out error))
            {
                probe = PsmfAudioProbeResult.Empty;
                return false;
            }

            probe = audio.Metadata;
            if (!probe.HasAudio)
            {
                error = "";
                return true;
            }

            var parent = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            using var output = new FileStream(
                outputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.SequentialScan);
            WriteOmaHeader(output, audio.CodecParameters);
            output.Write(audio.FrameBodies);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            probe = PsmfAudioProbeResult.Empty;
            error = ex.Message;
            return false;
        }
    }

    private static bool TryRead(Stream stream, out PsmfAudioStream audio, out string error)
    {
        audio = PsmfAudioStream.Empty;

        if (!stream.CanRead || !stream.CanSeek)
        {
            error = "PSMF input must be readable and seekable";
            return false;
        }

        if (!TryReadPsmfBounds(stream, out var streamStart, out var streamEnd, out error))
            return false;

        stream.Position = streamStart;
        var privateStreams = new Dictionary<byte, MemoryStream>();
        var privatePacketCounts = new Dictionary<byte, int>();

        while (stream.Position < streamEnd)
        {
            if (!TryReadStartCode(stream, streamEnd, out var streamId))
            {
                error = "PSMF packet boundary does not contain a start code";
                return false;
            }

            if (streamId == PackHeaderId)
            {
                if (!TrySkipPackHeader(stream, streamEnd))
                {
                    error = "PSMF pack header is truncated";
                    return false;
                }

                continue;
            }

            if (streamId == ProgramEndId)
                continue;

            // Every other top-level PSMF packet used by the corpus has a
            // 16-bit MPEG-PES length. Skipping by that stated length is what
            // prevents H.264 start-code emulation from being mistaken for PES.
            if (streamId is < 0xBB or > 0xEF)
            {
                error = $"Unsupported PSMF start code 0x{streamId:X2}";
                return false;
            }

            if (!TryReadPacketLength(stream, streamEnd, out var packetLength))
            {
                error = $"PSMF packet 0x{streamId:X2} is truncated";
                return false;
            }

            if (streamId != PrivateStream1Id)
            {
                stream.Position += packetLength;
                continue;
            }

            var packet = new byte[packetLength];
            stream.ReadExactly(packet);
            if (!TryGetPrivatePayload(packet, out var channel, out var payload, out error))
                return false;

            if (!privateStreams.TryGetValue(channel, out var elementaryStream))
            {
                elementaryStream = new MemoryStream();
                privateStreams.Add(channel, elementaryStream);
                privatePacketCounts.Add(channel, 0);
            }

            elementaryStream.Write(payload.Span);
            privatePacketCounts[channel]++;
        }

        if (privateStreams.Count == 0)
        {
            audio = PsmfAudioStream.Empty;
            error = "";
            return true;
        }

        var atracCandidates = privateStreams
            .Where(static pair => StartsWithAtracSync(pair.Value))
            .ToArray();
        if (atracCandidates.Length == 0)
        {
            error = "PSMF private stream does not contain ATRAC3+ frames";
            return false;
        }

        if (atracCandidates.Length != 1)
        {
            error = $"PSMF contains {atracCandidates.Length} ATRAC3+ streams; multiple-track muxing is not supported";
            return false;
        }

        var candidate = atracCandidates[0];
        if (!TryReadAtracFrames(
                candidate.Value.ToArray(),
                candidate.Key,
                privatePacketCounts[candidate.Key],
                out audio,
                out error))
        {
            return false;
        }

        return true;
    }

    private static bool TryReadPsmfBounds(
        Stream stream,
        out long streamStart,
        out long streamEnd,
        out string error)
    {
        streamStart = 0;
        streamEnd = 0;

        if (stream.Length < PsmfHeaderMinimumSize)
        {
            error = "PSMF header is truncated";
            return false;
        }

        Span<byte> header = stackalloc byte[PsmfHeaderMinimumSize];
        stream.Position = 0;
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("PSMF"u8))
        {
            error = "Not a PSMF container";
            return false;
        }

        streamStart = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
        var streamSize = BinaryPrimitives.ReadUInt32BigEndian(header[12..]);
        streamEnd = streamStart + streamSize;
        if (streamStart < PsmfHeaderMinimumSize || streamSize == 0 || streamEnd != stream.Length)
        {
            error = "PSMF header and stream sizes do not consume the file";
            return false;
        }

        error = "";
        return true;
    }

    private static bool TryGetPrivatePayload(
        byte[] packet,
        out byte channel,
        out ReadOnlyMemory<byte> payload,
        out string error)
    {
        channel = 0;
        payload = default;
        if (packet.Length < 7 || (packet[0] & 0xC0) != 0x80)
        {
            error = "PSMF private packet has an unsupported PES header";
            return false;
        }

        var payloadOffset = 3 + packet[2];
        if (payloadOffset >= packet.Length)
        {
            error = "PSMF private packet PES header is truncated";
            return false;
        }

        channel = packet[payloadOffset++];

        // sceMpeg's private-stream grammar: a three-byte PSP sub-header follows
        // the channel; 0xB0-0xBF channels carry one additional byte.
        var privateHeaderSize = channel is >= 0xB0 and <= 0xBF ? 4 : 3;
        if (payloadOffset + privateHeaderSize > packet.Length)
        {
            error = "PSMF private audio sub-header is truncated";
            return false;
        }

        payload = packet.AsMemory(payloadOffset + privateHeaderSize);
        error = "";
        return true;
    }

    private static bool TryReadAtracFrames(
        byte[] elementaryStream,
        byte privateStreamId,
        int packetCount,
        out PsmfAudioStream audio,
        out string error)
    {
        audio = PsmfAudioStream.Empty;
        if (elementaryStream.Length < AtracFrameHeaderSize)
        {
            error = "PSMF ATRAC3+ stream is truncated";
            return false;
        }

        var codecParameters = BinaryPrimitives.ReadUInt16BigEndian(elementaryStream.AsSpan(2));
        var channelId = (codecParameters >> 10) & 0x07;
        var sampleRateIndex = (codecParameters >> 13) & 0x07;
        if (channelId < 1 || channelId > ChannelCounts.Length || sampleRateIndex >= SampleRates.Length)
        {
            error = "PSMF ATRAC3+ frame declares an unsupported channel layout or sample rate";
            return false;
        }

        var frameSize = ((codecParameters & 0x03FF) * 8) + 16;
        if (frameSize <= AtracFrameHeaderSize)
        {
            error = "PSMF ATRAC3+ frame size is invalid";
            return false;
        }

        using var frameBodies = new MemoryStream(elementaryStream.Length);
        var frameCount = 0;
        for (var offset = 0; offset < elementaryStream.Length; offset += frameSize)
        {
            if (offset + frameSize > elementaryStream.Length)
            {
                error = "PSMF ATRAC3+ stream ends in a partial frame";
                return false;
            }

            var frame = elementaryStream.AsSpan(offset, frameSize);
            if (frame[0] != 0x0F || frame[1] != 0xD0)
            {
                error = $"PSMF ATRAC3+ sync is missing at frame {frameCount}";
                return false;
            }

            if (BinaryPrimitives.ReadUInt16BigEndian(frame[2..]) != codecParameters)
            {
                error = $"PSMF ATRAC3+ codec parameters change at frame {frameCount}";
                return false;
            }

            frameBodies.Write(frame[AtracFrameHeaderSize..]);
            frameCount++;
        }

        if (frameCount == 0)
        {
            error = "PSMF ATRAC3+ stream contains no complete frames";
            return false;
        }

        var probe = new PsmfAudioProbeResult(
            true,
            privateStreamId,
            packetCount,
            frameCount,
            frameSize,
            SampleRates[sampleRateIndex],
            ChannelCounts[channelId - 1],
            frameCount * SamplesPerFrame / (double)SampleRates[sampleRateIndex]);
        audio = new PsmfAudioStream(probe, codecParameters, frameBodies.ToArray());
        error = "";
        return true;
    }

    private static void WriteOmaHeader(Stream output, ushort codecParameters)
    {
        Span<byte> header = stackalloc byte[OmaHeaderSize];
        header.Clear();
        "EA3\0"u8.CopyTo(header);
        header[5] = OmaHeaderSize;
        header[6] = 0xFF;
        header[7] = 0xFF;
        header[32] = 1; // FFmpeg OMA codec id: ATRAC3+.
        BinaryPrimitives.WriteUInt16BigEndian(header[34..], codecParameters);
        output.Write(header);
    }

    private static bool StartsWithAtracSync(MemoryStream stream)
    {
        var buffer = stream.GetBuffer();
        return stream.Length >= 2 && buffer[0] == 0x0F && buffer[1] == 0xD0;
    }

    private static bool TryReadStartCode(Stream stream, long end, out byte streamId)
    {
        streamId = 0;
        Span<byte> startCode = stackalloc byte[4];
        if (end - stream.Position < startCode.Length)
            return false;

        stream.ReadExactly(startCode);
        if ((startCode[0] << 16 | startCode[1] << 8 | startCode[2]) != StartCodePrefix)
            return false;

        streamId = startCode[3];
        return true;
    }

    private static bool TrySkipPackHeader(Stream stream, long end)
    {
        Span<byte> header = stackalloc byte[10];
        if (end - stream.Position < header.Length)
            return false;

        stream.ReadExactly(header);
        var stuffingLength = header[9] & 0x07;
        if (end - stream.Position < stuffingLength)
            return false;

        stream.Position += stuffingLength;
        return true;
    }

    private static bool TryReadPacketLength(Stream stream, long end, out int packetLength)
    {
        packetLength = 0;
        if (end - stream.Position < sizeof(ushort))
            return false;

        Span<byte> length = stackalloc byte[sizeof(ushort)];
        stream.ReadExactly(length);
        packetLength = BinaryPrimitives.ReadUInt16BigEndian(length);
        return packetLength > 0 && end - stream.Position >= packetLength;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup for a staged OMA input.
        }
    }

    private sealed record PsmfAudioStream(
        PsmfAudioProbeResult Metadata,
        ushort CodecParameters,
        byte[] FrameBodies)
    {
        public static PsmfAudioStream Empty { get; } = new(
            PsmfAudioProbeResult.Empty,
            0,
            []);
    }
}

public sealed record PsmfAudioProbeResult(
    bool HasAudio,
    byte PrivateStreamId,
    int PacketCount,
    int FrameCount,
    int FrameSize,
    int SampleRate,
    int Channels,
    double DurationSeconds)
{
    internal static PsmfAudioProbeResult Empty { get; } = new(false, 0, 0, 0, 0, 0, 0, 0);
}
