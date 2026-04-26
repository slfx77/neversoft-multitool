using System.Buffers.Binary;
using NeversoftMultitool.Core.BinaryIO;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Extracts Sony ADS audio embedded inside PS2-style PSS private stream packets.
/// </summary>
public static class PssAudioExtractor
{
    private const byte PrivateStream1Id = 0xBD;
    private const byte PackHeaderId = 0xBA;
    private const byte ProgramStreamMapId = 0xBB;
    private const int StartCodePrefix = 0x000001;
    private const int AdsHeaderSize = 0x28;
    private const uint Pcm16Codec = 0x01;
    private const uint Pcm16CodecVariant = 0x80000001;
    private const uint PsxAdpcmCodec = 0x10;
    private const uint PsxAdpcmCodecVariant = 0x02;

    public static PssAudioProbeResult? Probe(string inputPath)
    {
        using var stream = File.OpenRead(inputPath);
        return TryReadAdsStream(stream, out var adsStream, out _)
            ? new PssAudioProbeResult(
                GetCodecName(adsStream.Codec),
                adsStream.SampleRate,
                adsStream.Channels,
                adsStream.Interleave)
            : null;
    }

    public static AudioConvertResult ConvertToWav(string inputPath, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(inputPath) + ".wav");

        using var stream = File.OpenRead(inputPath);
        return WriteWavFromStream(stream, outputPath);
    }

    /// <summary>In-memory variant of <see cref="ConvertToWav(string, string)"/>.</summary>
    public static AudioConvertResult ConvertToWav(byte[] data, string stem, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir, stem + ".wav");
        using var stream = new MemoryStream(data, writable: false);
        return WriteWavFromStream(stream, outputPath);
    }

    private static AudioConvertResult WriteWavFromStream(Stream stream, string outputPath)
    {
        return TryWriteWav(stream, outputPath, out var error)
            ? new AudioConvertResult { Success = true, SamplesWritten = 1 }
            : new AudioConvertResult { ErrorMessage = error };
    }

    internal static bool TryWriteWav(string inputPath, string outputPath, out string error)
    {
        using var stream = File.OpenRead(inputPath);
        return TryWriteWav(stream, outputPath, out error);
    }

    internal static bool TryWriteWav(Stream stream, string outputPath, out string error)
    {
        if (!TryReadAdsStream(stream, out var adsStream, out error))
            return false;

        if (!TryDecodeAdsStream(adsStream, out var pcmSamples, out error))
            return false;

        WavWriter.WritePcm16(outputPath, adsStream.SampleRate, adsStream.Channels, pcmSamples);
        return true;
    }

    private static bool TryReadAdsStream(Stream stream, out AdsStream adsStream, out string error)
    {
        adsStream = default;

        try
        {
            using var adsBytes = new MemoryStream();
            byte? audioStreamId = null;
            var sawAdsHeader = false;

            while (TryReadNextPacket(stream, out var streamId, out var packet))
            {
                if (streamId != PrivateStream1Id)
                    continue;

                if (!TryFindPrivateAudioPayload(packet, out var candidateStreamId, out var payload))
                    continue;

                if (audioStreamId == null)
                    audioStreamId = candidateStreamId;

                if (candidateStreamId != audioStreamId.Value)
                    continue;

                if (!sawAdsHeader)
                {
                    var adsOffset = payload.IndexOf("SShd"u8);
                    if (adsOffset < 0)
                        continue;

                    adsBytes.Write(payload[adsOffset..]);
                    sawAdsHeader = true;
                }
                else
                {
                    adsBytes.Write(payload);
                }
            }

            if (!sawAdsHeader)
            {
                error = "PSS private-stream audio header not found";
                return false;
            }

            if (!TryParseAdsStream(adsBytes.ToArray(), out adsStream, out error))
                return false;

            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryDecodeAdsStream(AdsStream adsStream, out short[] pcmSamples, out string error)
    {
        switch (adsStream.Codec)
        {
            case Pcm16Codec:
            case Pcm16CodecVariant:
                pcmSamples = DecodeInterleavedPcm16(adsStream.Body, adsStream.Channels, adsStream.Interleave);
                error = "";
                return true;

            case PsxAdpcmCodec:
            case PsxAdpcmCodecVariant:
                pcmSamples = DecodeInterleavedPsxAdpcm(adsStream.Body, adsStream.Channels, adsStream.Interleave);
                error = "";
                return true;

            default:
                pcmSamples = [];
                error = $"Unsupported ADS codec 0x{adsStream.Codec:X8}";
                return false;
        }
    }

    private static bool TryParseAdsStream(byte[] adsBytes, out AdsStream adsStream, out string error)
    {
        adsStream = default;

        if (adsBytes.Length < AdsHeaderSize)
        {
            error = "ADS header is truncated";
            return false;
        }

        if (!adsBytes.AsSpan(0, 4).SequenceEqual("SShd"u8) ||
            !adsBytes.AsSpan(0x20, 4).SequenceEqual("SSbd"u8))
        {
            error = "Embedded ADS header is invalid";
            return false;
        }

        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(adsBytes.AsSpan(4, 4));
        var codec = BinaryPrimitives.ReadUInt32LittleEndian(adsBytes.AsSpan(8, 4));
        var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(adsBytes.AsSpan(0x0C, 4));
        var channels = BinaryPrimitives.ReadInt32LittleEndian(adsBytes.AsSpan(0x10, 4));
        var interleave = BinaryPrimitives.ReadInt32LittleEndian(adsBytes.AsSpan(0x14, 4));
        var bodySize = BinaryPrimitives.ReadInt32LittleEndian(adsBytes.AsSpan(0x24, 4));

        if (headerSize < 0x18 || headerSize > 0x20)
        {
            error = $"Unsupported ADS header size 0x{headerSize:X}";
            return false;
        }

        if (sampleRate <= 0 || sampleRate > 192000)
        {
            error = $"Invalid ADS sample rate {sampleRate}";
            return false;
        }

        if (channels <= 0 || channels > 8)
        {
            error = $"Invalid ADS channel count {channels}";
            return false;
        }

        if (bodySize <= 0)
        {
            error = "ADS body size is invalid";
            return false;
        }

        var availableBodySize = Math.Min(bodySize, adsBytes.Length - AdsHeaderSize);
        if (availableBodySize <= 0)
        {
            error = "ADS body is empty";
            return false;
        }

        adsStream = new AdsStream(
            codec,
            sampleRate,
            channels,
            interleave,
            adsBytes.AsMemory(AdsHeaderSize, availableBodySize));
        error = "";
        return true;
    }

    private static short[] DecodeInterleavedPcm16(ReadOnlyMemory<byte> body, int channels, int interleave)
    {
        var bodySpan = body.Span;
        if (channels == 1 || interleave <= 0)
            return ConvertLittleEndianPcmToShorts(bodySpan);

        var samples = new List<short>(bodySpan.Length / 2);
        var frameSize = channels * interleave;

        for (var frameBase = 0; frameBase < bodySpan.Length; frameBase += frameSize)
        {
            for (var sampleOffset = 0; sampleOffset < interleave; sampleOffset += 2)
            {
                var wroteSample = false;

                for (var channel = 0; channel < channels; channel++)
                {
                    var position = frameBase + (channel * interleave) + sampleOffset;
                    if (position + 1 >= bodySpan.Length)
                        continue;

                    samples.Add(BinaryPrimitives.ReadInt16LittleEndian(bodySpan.Slice(position, 2)));
                    wroteSample = true;
                }

                if (!wroteSample)
                    break;
            }
        }

        return samples.ToArray();
    }

    private static short[] DecodeInterleavedPsxAdpcm(ReadOnlyMemory<byte> body, int channels, int interleave)
    {
        var channelData = DeinterleaveChannelBlocks(body, channels, interleave);
        var decodedChannels = new short[channelData.Count][];
        var maxSampleCount = 0;

        for (var i = 0; i < channelData.Count; i++)
        {
            decodedChannels[i] = SpuAdpcm.Decode(channelData[i]);
            maxSampleCount = Math.Max(maxSampleCount, decodedChannels[i].Length);
        }

        var interleaved = new short[maxSampleCount * channels];
        var writeIndex = 0;

        for (var sampleIndex = 0; sampleIndex < maxSampleCount; sampleIndex++)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                interleaved[writeIndex++] = sampleIndex < decodedChannels[channel].Length
                    ? decodedChannels[channel][sampleIndex]
                    : (short)0;
            }
        }

        return interleaved;
    }

    private static List<byte[]> DeinterleaveChannelBlocks(ReadOnlyMemory<byte> body, int channels, int interleave)
    {
        var bodySpan = body.Span;
        if (channels <= 1 || interleave <= 0)
            return [bodySpan.ToArray()];

        var perChannel = Enumerable.Range(0, channels)
            .Select(_ => new MemoryStream())
            .ToArray();

        var frameSize = channels * interleave;
        for (var frameBase = 0; frameBase < bodySpan.Length; frameBase += frameSize)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                var blockStart = frameBase + (channel * interleave);
                if (blockStart >= bodySpan.Length)
                    continue;

                var blockLength = Math.Min(interleave, bodySpan.Length - blockStart);
                perChannel[channel].Write(bodySpan.Slice(blockStart, blockLength));
            }
        }

        return perChannel.Select(static stream => stream.ToArray()).ToList();
    }

    private static short[] ConvertLittleEndianPcmToShorts(ReadOnlySpan<byte> data)
    {
        var sampleCount = data.Length / 2;
        var samples = new short[sampleCount];

        for (var i = 0; i < sampleCount; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(i * 2, 2));

        return samples;
    }

    private static bool TryFindPrivateAudioPayload(
        byte[] packet,
        out byte audioStreamId,
        out ReadOnlySpan<byte> payload)
    {
        var searchLength = Math.Min(packet.Length, 32);
        for (var i = 0; i <= searchLength - 3; i++)
        {
            var candidate = packet[i];
            if (candidate is < 0xA0 or > 0xAF)
                continue;

            if (packet[i + 1] != 0x00 || packet[i + 2] != 0x00)
                continue;

            audioStreamId = candidate;
            payload = packet.AsSpan(i + 3);
            return true;
        }

        audioStreamId = 0;
        payload = default;
        return false;
    }

    private static bool TryReadNextPacket(Stream stream, out byte streamId, out byte[] packet)
    {
        packet = [];
        streamId = 0;

        while (TrySeekNextStartCode(stream, out streamId))
        {
            switch (streamId)
            {
                case PackHeaderId:
                    if (!TrySkipPackHeader(stream))
                        return false;
                    continue;

                case ProgramStreamMapId:
                case PrivateStream1Id:
                case >= 0xBC and <= 0xEF:
                {
                    var packetLength = ReadUInt16BigEndian(stream);
                    if (packetLength < 0)
                        return false;

                    packet = new byte[packetLength];
                    var bytesRead = stream.Read(packet, 0, packetLength);
                    if (bytesRead != packetLength)
                        return false;

                    return true;
                }

                default:
                    continue;
            }
        }

        return false;
    }

    private static bool TrySkipPackHeader(Stream stream)
    {
        Span<byte> header = stackalloc byte[10];
        if (stream.Read(header) != header.Length)
            return false;

        var stuffingLength = header[9] & 0x07;
        return SkipBytes(stream, stuffingLength);
    }

    private static bool TrySeekNextStartCode(Stream stream, out byte streamId)
    {
        streamId = 0;
        var state = 0x00FFFFFF;

        while (true)
        {
            var nextByte = stream.ReadByte();
            if (nextByte < 0)
                return false;

            state = ((state << 8) | (nextByte & 0xFF)) & 0x00FFFFFF;
            if (state != StartCodePrefix)
                continue;

            var id = stream.ReadByte();
            if (id < 0)
                return false;

            streamId = (byte)id;
            return true;
        }
    }

    private static int ReadUInt16BigEndian(Stream stream)
    {
        var high = stream.ReadByte();
        var low = stream.ReadByte();
        return high < 0 || low < 0 ? -1 : (high << 8) | low;
    }

    private static bool SkipBytes(Stream stream, int bytesToSkip)
    {
        if (bytesToSkip <= 0)
            return true;

        if (stream.CanSeek)
        {
            if (stream.Position + bytesToSkip > stream.Length)
                return false;

            stream.Position += bytesToSkip;
            return true;
        }

        Span<byte> buffer = stackalloc byte[256];
        var remaining = bytesToSkip;
        while (remaining > 0)
        {
            var read = stream.Read(buffer[..Math.Min(buffer.Length, remaining)]);
            if (read <= 0)
                return false;

            remaining -= read;
        }

        return true;
    }

    private static string GetCodecName(uint codec)
    {
        return codec switch
        {
            Pcm16Codec or Pcm16CodecVariant => "ADS PCM16LE",
            PsxAdpcmCodec or PsxAdpcmCodecVariant => "ADS PSX-ADPCM",
            _ => $"ADS 0x{codec:X8}"
        };
    }

    private readonly record struct AdsStream(
        uint Codec,
        int SampleRate,
        int Channels,
        int Interleave,
        ReadOnlyMemory<byte> Body);

    public sealed record PssAudioProbeResult(
        string CodecName,
        int SampleRate,
        int Channels,
        int Interleave);
}
