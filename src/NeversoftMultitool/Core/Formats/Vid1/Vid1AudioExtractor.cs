using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Core.Formats.Vid1;

/// <summary>
///     Extracts custom Vorbis audio carried inside Factor 5 VID1 movie containers.
/// </summary>
public static class Vid1AudioExtractor
{
    private const int HeadChildOffset = 0x0C;
    private const int FrameHeaderSize = 0x20;
    private const int AuddPacketDataOffset = 0x14;
    private const uint OggCrcPolynomial = 0x04C11DB7;

    public static Vid1AudioProbeResult? Probe(string inputPath)
    {
        return TryProbe(inputPath, out var probe, out _)
            ? probe
            : null;
    }

    public static AudioConvertResult ConvertToWav(string inputPath, string outputDir)
    {
        try
        {
            return ConvertToWav(File.ReadAllBytes(inputPath), Path.GetFileNameWithoutExtension(inputPath), outputDir);
        }
        catch (Exception ex)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
    }

    /// <summary>In-memory variant of <see cref="ConvertToWav(string, string)" />.</summary>
    public static AudioConvertResult ConvertToWav(byte[] data, string stem, string outputDir)
    {
        var ffmpeg = SfdConverter.FindFfmpeg();
        if (ffmpeg == null)
            return new AudioConvertResult { ErrorMessage = "ffmpeg not found on PATH" };

        if (!TryParseVid1(data, out var stream, out var error))
            return new AudioConvertResult { ErrorMessage = error };

        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir, stem + ".wav");
        var tempOggPath = Path.Combine(
            Path.GetTempPath(),
            "NeversoftMultitool",
            "Vid1Audio",
            $"{Guid.NewGuid():N}_{stem}.ogg");

        try
        {
            var tempDir = Path.GetDirectoryName(tempOggPath);
            if (!string.IsNullOrWhiteSpace(tempDir))
                Directory.CreateDirectory(tempDir);

            if (!TryWriteOggStream(stream, tempOggPath, out error))
                return new AudioConvertResult { ErrorMessage = error };

            if (!TryDecodeOggToWav(ffmpeg, tempOggPath, outputPath, out error))
            {
                TryDeleteFile(outputPath);
                return new AudioConvertResult { ErrorMessage = error };
            }

            return new AudioConvertResult { Success = true, SamplesWritten = 1 };
        }
        finally
        {
            TryDeleteFile(tempOggPath);
        }
    }

    internal static bool TryDecodeToPcm16(string inputPath, out Vid1PcmAudio? audio, out string error)
    {
        audio = null;

        var ffmpeg = SfdConverter.FindFfmpeg();
        if (ffmpeg == null)
        {
            error = "ffmpeg not found on PATH";
            return false;
        }

        if (!TryReadStream(inputPath, out var stream, out error))
            return false;

        var tempRoot = Path.Combine(Path.GetTempPath(), "NeversoftMultitool", "Vid1Audio");
        var tempStem = $"{Guid.NewGuid():N}_{Path.GetFileNameWithoutExtension(inputPath)}";
        var tempOggPath = Path.Combine(tempRoot, tempStem + ".ogg");
        var tempPcmPath = Path.Combine(tempRoot, tempStem + ".s16le");

        try
        {
            Directory.CreateDirectory(tempRoot);

            if (!TryWriteOggStream(stream, tempOggPath, out error))
                return false;

            if (!TryDecodeOggToPcm16(ffmpeg, tempOggPath, tempPcmPath, out error))
                return false;

            var pcm = File.ReadAllBytes(tempPcmPath);
            audio = new Vid1PcmAudio(pcm, stream.SampleRate, stream.Channels, stream.TotalSamples);
            error = "";
            return true;
        }
        finally
        {
            TryDeleteFile(tempOggPath);
            TryDeleteFile(tempPcmPath);
        }
    }

    internal static bool TryProbe(string inputPath, out Vid1AudioProbeResult? probe, out string error)
    {
        probe = null;
        if (!TryReadStream(inputPath, out var stream, out error))
            return false;

        probe = new Vid1AudioProbeResult("VID1 Vorbis", stream.SampleRate, stream.Channels, stream.TotalSamples);
        error = "";
        return true;
    }

    internal static bool TryReadPacketHeader(
        ReadOnlySpan<byte> data,
        int offset,
        int endOffset,
        out int packetDataOffset,
        out int packetSize)
    {
        packetDataOffset = 0;
        packetSize = 0;

        if (offset < 0 || offset >= endOffset || endOffset > data.Length)
            return false;

        var bitOffset = offset * 8;
        var bitEnd = endOffset * 8;

        if (!TryReadBitsLsb(data, ref bitOffset, bitEnd, 4, out var sizeBits))
            return false;

        if (!TryReadBitsLsb(data, ref bitOffset, bitEnd, sizeBits + 1, out packetSize))
            return false;

        if (sizeBits == 0 && packetSize == 0 && data[offset] == 0x80)
            packetSize = 1;

        bitOffset = AlignToNextByte(bitOffset);
        packetDataOffset = bitOffset / 8;

        return packetSize >= 0 &&
               packetDataOffset >= offset &&
               packetDataOffset <= endOffset &&
               packetDataOffset + packetSize <= endOffset;
    }

    private static bool TryReadStream(string inputPath, out Vid1AudioStream stream, out string error)
    {
        stream = default;

        try
        {
            var data = File.ReadAllBytes(inputPath);
            return TryParseVid1(data, out stream, out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryParseVid1(byte[] data, out Vid1AudioStream stream, out string error)
    {
        stream = default;

        if (!TryReadChunk(data, 0, data.Length, out var rootChunk, out error))
            return false;

        if (rootChunk.Tag != "VID1")
        {
            error = "Not a VID1 file";
            return false;
        }

        if (!TryReadChunk(data, rootChunk.EndOffset, data.Length, out var headChunk, out error))
            return false;

        if (headChunk.Tag != "HEAD")
        {
            error = "VID1 HEAD chunk not found";
            return false;
        }

        if (!TryFindChunk(data, headChunk.Offset + HeadChildOffset, headChunk.EndOffset, "AUDH", out var audhChunk,
                out error))
            return false;

        if (!TryParseAudioHeader(data, audhChunk, out var sampleRate, out var channels, out var totalSamples,
                out var idPacket, out var setupPacket, out error))
            return false;

        var audioPackets = new List<byte[]>();
        var scanOffset = headChunk.EndOffset;

        while (scanOffset + 8 <= data.Length)
        {
            if (!TryReadChunk(data, scanOffset, data.Length, out var chunk, out error))
            {
                if (IsZeroPadding(data, scanOffset))
                    break;

                return false;
            }

            if (chunk.Tag == "FRAM" && !TryCollectFrameAudioPackets(data, chunk, audioPackets, out error))
                return false;

            scanOffset = chunk.EndOffset;
        }

        if (audioPackets.Count == 0)
        {
            error = "VID1 audio packets were not found";
            return false;
        }

        stream = new Vid1AudioStream(sampleRate, channels, totalSamples, idPacket, setupPacket, audioPackets);
        error = "";
        return true;
    }

    private static bool TryCollectFrameAudioPackets(
        byte[] data,
        Vid1Chunk frameChunk,
        List<byte[]> audioPackets,
        out string error)
    {
        error = "";
        var childOffset = FindFrameChildOffset(data, frameChunk);
        if (childOffset < 0)
            return true;

        while (childOffset + 8 <= frameChunk.EndOffset)
        {
            if (!TryReadChunk(data, childOffset, frameChunk.EndOffset, out var chunk, out error))
                return false;

            if (chunk.Tag == "AUDD" && !TryCollectAudioBlockPackets(data, chunk, audioPackets, out error))
                return false;

            childOffset = chunk.EndOffset;
        }

        error = "";
        return true;
    }

    private static int FindFrameChildOffset(byte[] data, Vid1Chunk frameChunk)
    {
        var preferred = frameChunk.Offset + FrameHeaderSize;
        if (preferred + 8 <= frameChunk.EndOffset &&
            TryReadChunk(data, preferred, frameChunk.EndOffset, out var firstChild, out _) &&
            IsFramePayloadTag(firstChild.Tag))
        {
            return preferred;
        }

        var searchEnd = Math.Min(frameChunk.EndOffset - 8, frameChunk.Offset + 0x40);
        for (var offset = frameChunk.Offset + HeadChildOffset; offset <= searchEnd; offset++)
        {
            if (!LooksLikeFramePayloadTag(data, offset))
                continue;

            if (TryReadChunk(data, offset, frameChunk.EndOffset, out var childChunk, out _) &&
                IsFramePayloadTag(childChunk.Tag))
                return offset;
        }

        return -1;
    }

    private static bool TryCollectAudioBlockPackets(
        byte[] data,
        Vid1Chunk auddChunk,
        List<byte[]> audioPackets,
        out string error)
    {
        error = "";

        if (auddChunk.Size < AuddPacketDataOffset)
        {
            error = "AUDD chunk is truncated";
            return false;
        }

        if (auddChunk.Offset + 0x10 > auddChunk.EndOffset)
        {
            error = "AUDD chunk header is truncated";
            return false;
        }

        var packetDataStart = auddChunk.Offset + AuddPacketDataOffset;
        var declaredPacketBytes = checked((int)ReadUInt32BigEndian(data, auddChunk.Offset + 0x0C)) - 6;
        if (declaredPacketBytes <= 0)
            return true;

        var packetDataEnd = Math.Min(auddChunk.EndOffset, packetDataStart + declaredPacketBytes);
        var currentOffset = packetDataStart;

        while (currentOffset < packetDataEnd)
        {
            if (!TryReadPacketHeader(data, currentOffset, packetDataEnd, out var packetOffset, out var packetSize))
            {
                error = "VID1 audio packet header is invalid";
                return false;
            }

            if (packetSize > 0)
                audioPackets.Add(data.AsSpan(packetOffset, packetSize).ToArray());

            currentOffset = packetOffset + packetSize;
        }

        return true;
    }

    private static bool TryParseAudioHeader(
        byte[] data,
        Vid1Chunk audhChunk,
        out int sampleRate,
        out int channels,
        out int totalSamples,
        out byte[] idPacket,
        out byte[] setupPacket,
        out string error)
    {
        sampleRate = 0;
        channels = 0;
        totalSamples = 0;
        idPacket = [];
        setupPacket = [];

        var metadataOffset = audhChunk.Offset + HeadChildOffset;
        if (metadataOffset + 0x24 >= audhChunk.EndOffset)
        {
            error = "AUDH chunk is truncated";
            return false;
        }

        if (!data.AsSpan(metadataOffset, 4).SequenceEqual("VAUD"u8))
        {
            error = "VID1 audio codec is not VAUD";
            return false;
        }

        sampleRate = checked((int)ReadUInt32BigEndian(data, metadataOffset + 0x04));
        channels = data[metadataOffset + 0x08];
        totalSamples = checked((int)ReadUInt32BigEndian(data, metadataOffset + 0x20));

        if (sampleRate <= 0 || sampleRate > 192000)
        {
            error = $"Invalid VID1 sample rate {sampleRate}";
            return false;
        }

        if (channels <= 0 || channels > 8)
        {
            error = $"Invalid VID1 channel count {channels}";
            return false;
        }

        var headerPacketOffset = metadataOffset + 0x24;
        if (!TryReadPacketHeader(data, headerPacketOffset, audhChunk.EndOffset, out var firstPacketOffset,
                out var firstPacketSize))
        {
            error = "VID1 Vorbis identification header is invalid";
            return false;
        }

        idPacket = data.AsSpan(firstPacketOffset, firstPacketSize).ToArray();
        var secondPacketHeaderOffset = firstPacketOffset + firstPacketSize;

        if (!TryReadPacketHeader(data, secondPacketHeaderOffset, audhChunk.EndOffset, out var secondPacketOffset,
                out var secondPacketSize))
        {
            error = "VID1 Vorbis setup header is invalid";
            return false;
        }

        setupPacket = data.AsSpan(secondPacketOffset, secondPacketSize).ToArray();

        if (!LooksLikeVorbisPacket(idPacket, 0x01) || !LooksLikeVorbisPacket(setupPacket, 0x05))
        {
            error = "VID1 Vorbis headers are malformed";
            return false;
        }

        error = "";
        return true;
    }

    private static bool TryFindChunk(
        byte[] data,
        int startOffset,
        int endOffset,
        string tag,
        out Vid1Chunk chunk,
        out string error)
    {
        chunk = default;
        error = "";
        var currentOffset = startOffset;

        while (currentOffset + 8 <= endOffset)
        {
            if (!TryReadChunk(data, currentOffset, endOffset, out var candidate, out error))
                return false;

            if (candidate.Tag == tag)
            {
                chunk = candidate;
                error = "";
                return true;
            }

            currentOffset = candidate.EndOffset;
        }

        error = $"VID1 chunk {tag} not found";
        return false;
    }

    private static bool TryReadChunk(
        byte[] data,
        int offset,
        int limit,
        out Vid1Chunk chunk,
        out string error)
    {
        chunk = default;

        if (offset < 0 || offset + 8 > limit || offset + 8 > data.Length)
        {
            error = "VID1 chunk header is truncated";
            return false;
        }

        var size = checked((int)ReadUInt32BigEndian(data, offset + 4));
        if (size < 8)
        {
            error = "VID1 chunk size is invalid";
            return false;
        }

        var endOffset = offset + size;
        if (endOffset > limit || endOffset > data.Length)
        {
            error = "VID1 chunk extends beyond the file";
            return false;
        }

        chunk = new Vid1Chunk(
            Encoding.ASCII.GetString(data, offset, 4),
            offset,
            size,
            endOffset);
        error = "";
        return true;
    }

    private static bool TryWriteOggStream(Vid1AudioStream stream, string outputPath, out string error)
    {
        error = "";

        try
        {
            using var output = File.Create(outputPath);
            var packets = new List<byte[]>(3 + stream.AudioPackets.Count)
            {
                stream.IdPacket,
                BuildCommentPacket("NeversoftMultitool"),
                stream.SetupPacket
            };
            packets.AddRange(stream.AudioPackets);

            const uint serialNumber = 0x31564944;
            uint pageSequence = 0;

            for (var i = 0; i < packets.Count; i++)
            {
                var packet = packets[i];
                if (packet.Length > 255 * 255)
                {
                    error = "VID1 Vorbis packet is too large for the Ogg page writer";
                    return false;
                }

                byte headerType = 0;
                if (i == 0)
                    headerType |= 0x02;
                if (i == packets.Count - 1)
                    headerType |= 0x04;

                ulong granulePosition = 0;
                if (i >= 3)
                {
                    var audioPacketIndex = i - 3;
                    if (stream.TotalSamples > 0)
                    {
                        granulePosition = audioPacketIndex == stream.AudioPackets.Count - 1
                            ? (ulong)stream.TotalSamples
                            : (ulong)Math.Max(
                                1,
                                (int)Math.Round(
                                    (double)stream.TotalSamples * (audioPacketIndex + 1) / stream.AudioPackets.Count));
                    }
                    else
                    {
                        granulePosition = (ulong)(audioPacketIndex + 1);
                    }
                }

                WriteOggPage(output, packet, headerType, granulePosition, serialNumber, pageSequence++);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void WriteOggPage(
        Stream output,
        byte[] packet,
        byte headerType,
        ulong granulePosition,
        uint serialNumber,
        uint sequenceNumber)
    {
        var lacingValues = BuildLacingValues(packet.Length);
        var header = new byte[27 + lacingValues.Length];

        "OggS"u8.CopyTo(header.AsSpan(0, 4));
        header[4] = 0;
        header[5] = headerType;
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(6, 8), granulePosition);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14, 4), serialNumber);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(18, 4), sequenceNumber);
        header[26] = checked((byte)lacingValues.Length);
        lacingValues.CopyTo(header.AsSpan(27));

        var crc = ComputeOggCrc(header, packet);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(22, 4), crc);

        output.Write(header);
        output.Write(packet);
    }

    private static byte[] BuildLacingValues(int packetLength)
    {
        var fullSegments = packetLength / 255;
        var remainder = packetLength % 255;
        var segmentCount = fullSegments + 1;
        var lacingValues = new byte[segmentCount];

        for (var i = 0; i < fullSegments; i++)
            lacingValues[i] = 255;

        lacingValues[^1] = checked((byte)remainder);
        return lacingValues;
    }

    private static uint ComputeOggCrc(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload)
    {
        var crc = 0u;
        crc = UpdateOggCrc(crc, header[..22]);
        crc = UpdateOggCrc(crc, [0, 0, 0, 0]);
        crc = UpdateOggCrc(crc, header[26..]);
        crc = UpdateOggCrc(crc, payload);
        return crc;
    }

    private static uint UpdateOggCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            crc ^= (uint)value << 24;
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x80000000) != 0
                    ? (crc << 1) ^ OggCrcPolynomial
                    : crc << 1;
        }

        return crc;
    }

    private static byte[] BuildCommentPacket(string vendor)
    {
        var vendorBytes = Encoding.UTF8.GetBytes(vendor);
        var packet = new byte[1 + 6 + 4 + vendorBytes.Length + 4 + 1];
        packet[0] = 0x03;
        "vorbis"u8.CopyTo(packet.AsSpan(1, 6));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(7, 4), checked((uint)vendorBytes.Length));
        vendorBytes.CopyTo(packet.AsSpan(11, vendorBytes.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(11 + vendorBytes.Length, 4), 0);
        packet[^1] = 0x01;
        return packet;
    }

    private static bool TryDecodeOggToWav(string ffmpeg, string oggPath, string wavPath, out string error)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = $"-y -loglevel error -i \"{oggPath}\" -acodec pcm_s16le \"{wavPath}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        process.Start();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(120_000);

        if (process.ExitCode == 0 && File.Exists(wavPath))
        {
            error = "";
            return true;
        }

        error = string.IsNullOrWhiteSpace(stderr)
            ? $"ffmpeg exited with code {process.ExitCode}"
            : stderr.Trim();
        return false;
    }

    private static bool TryDecodeOggToPcm16(string ffmpeg, string oggPath, string pcmPath, out string error)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = $"-y -loglevel error -i \"{oggPath}\" -f s16le -acodec pcm_s16le \"{pcmPath}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        process.Start();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(120_000);

        if (process.ExitCode == 0 && File.Exists(pcmPath))
        {
            error = "";
            return true;
        }

        error = string.IsNullOrWhiteSpace(stderr)
            ? $"ffmpeg exited with code {process.ExitCode}"
            : stderr.Trim();
        return false;
    }

    private static bool LooksLikeFramePayloadTag(byte[] data, int offset)
    {
        return offset + 4 <= data.Length &&
               (data.AsSpan(offset, 4).SequenceEqual("VIDD"u8) || data.AsSpan(offset, 4).SequenceEqual("AUDD"u8));
    }

    private static bool IsZeroPadding(byte[] data, int offset)
    {
        for (var i = offset; i < data.Length; i++)
        {
            if (data[i] != 0)
                return false;
        }

        return true;
    }

    private static bool IsFramePayloadTag(string tag)
    {
        return tag is "VIDD" or "AUDD";
    }

    private static bool LooksLikeVorbisPacket(byte[] packet, byte expectedType)
    {
        return packet.Length >= 7 &&
               packet[0] == expectedType &&
               packet.AsSpan(1, 6).SequenceEqual("vorbis"u8);
    }

    private static bool TryReadBitsLsb(ReadOnlySpan<byte> data, ref int bitOffset, int bitEnd, int bitCount,
        out int value)
    {
        value = 0;
        if (bitOffset + bitCount > bitEnd)
            return false;

        for (var i = 0; i < bitCount; i++)
        {
            var absoluteBit = bitOffset + i;
            var byteIndex = absoluteBit / 8;
            var bitIndex = absoluteBit % 8;
            value |= ((data[byteIndex] >> bitIndex) & 1) << i;
        }

        bitOffset += bitCount;
        return true;
    }

    private static int AlignToNextByte(int bitOffset)
    {
        return (bitOffset + 7) & ~7;
    }

    private static uint ReadUInt32BigEndian(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
    }

    private static void TryDeleteFile(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path))
                File.Delete(path);
        }
        catch
        {
            /* best effort */
        }
    }

    private readonly record struct Vid1Chunk(
        string Tag,
        int Offset,
        int Size,
        int EndOffset);

    private readonly record struct Vid1AudioStream(
        int SampleRate,
        int Channels,
        int TotalSamples,
        byte[] IdPacket,
        byte[] SetupPacket,
        List<byte[]> AudioPackets);

    internal sealed record Vid1PcmAudio(
        byte[] Pcm16,
        int SampleRate,
        int Channels,
        int TotalSamples)
    {
        public int BytesPerSecond => SampleRate * Channels * 2;

        public TimeSpan Duration => TotalSamples > 0 && SampleRate > 0
            ? TimeSpan.FromSeconds((double)TotalSamples / SampleRate)
            : TimeSpan.FromSeconds(BytesPerSecond > 0 ? (double)Pcm16.Length / BytesPerSecond : 0);
    }

    public sealed record Vid1AudioProbeResult(
        string CodecName,
        int SampleRate,
        int Channels,
        int TotalSamples);
}
