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

    public static Vid1AudioProbeResult? Probe(string inputPath, int trackIndex = 0)
    {
        return TryProbe(inputPath, out var probe, out _, trackIndex)
            ? probe
            : null;
    }

    /// <summary>In-memory variant of <see cref="Probe(string, int)" />.</summary>
    public static Vid1AudioProbeResult? Probe(byte[] data, int trackIndex = 0)
    {
        try
        {
            if (!TryParseVid1(data, out var tracks, out _) ||
                trackIndex < 0 || trackIndex >= tracks.Count)
                return null;

            return CreateProbe(tracks[trackIndex], tracks.Count);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Probes every audio track in a VID1 file (e.g. one per dubbed language).</summary>
    public static IReadOnlyList<Vid1AudioProbeResult> ProbeTracks(string inputPath)
    {
        return TryProbeTracks(inputPath, out var probes, out _)
            ? probes
            : [];
    }

    /// <summary>In-memory variant of <see cref="ProbeTracks(string)" />.</summary>
    public static IReadOnlyList<Vid1AudioProbeResult> ProbeTracks(byte[] data)
    {
        try
        {
            if (!TryParseVid1(data, out var tracks, out _))
                return [];

            return tracks.Select(track => CreateProbe(track, tracks.Count)).ToArray();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    ///     Converts VID1 audio to WAV. If <paramref name="trackIndex" /> is null, every audio
    ///     track is converted (one file per dubbed language track); otherwise only the
    ///     requested track is written. The first track is always named "{stem}.wav" for
    ///     backwards compatibility with single-track files; additional tracks are named
    ///     "{stem}_track{N}.wav".
    /// </summary>
    public static AudioConvertResult ConvertToWav(string inputPath, string outputDir, int? trackIndex = null)
    {
        try
        {
            return ConvertToWav(
                File.ReadAllBytes(inputPath),
                Path.GetFileNameWithoutExtension(inputPath),
                outputDir,
                trackIndex);
        }
        catch (Exception ex)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
    }

    /// <summary>In-memory variant of <see cref="ConvertToWav(string, string, int?)" />.</summary>
    public static AudioConvertResult ConvertToWav(byte[] data, string stem, string outputDir, int? trackIndex = null)
    {
        var ffmpeg = SfdConverter.FindFfmpeg();
        if (ffmpeg == null)
            return new AudioConvertResult { ErrorMessage = "ffmpeg not found on PATH" };

        if (!TryParseVid1(data, out var tracks, out var error))
            return new AudioConvertResult { ErrorMessage = error };

        if (trackIndex is { } requestedTrack)
        {
            if (requestedTrack < 0 || requestedTrack >= tracks.Count)
                return new AudioConvertResult { ErrorMessage = $"VID1 track {requestedTrack} does not exist" };

            return ConvertTrackToWav(tracks[requestedTrack], TrackOutputPath(outputDir, stem, requestedTrack), ffmpeg,
                out error)
                ? new AudioConvertResult { Success = true, SamplesWritten = 1 }
                : new AudioConvertResult { ErrorMessage = error };
        }

        Directory.CreateDirectory(outputDir);
        var written = 0;
        foreach (var track in tracks)
        {
            if (!ConvertTrackToWav(track, TrackOutputPath(outputDir, stem, track.TrackIndex), ffmpeg, out error))
                return new AudioConvertResult { ErrorMessage = error };

            written++;
        }

        return new AudioConvertResult { Success = true, SamplesWritten = written };
    }

    /// <summary>
    ///     The WAV file name <see cref="ConvertToWav(string, string, int?)" /> writes for a given
    ///     track: "{stem}.wav" for the first track, "{stem}_track{N}.wav" for the rest.
    /// </summary>
    public static string GetTrackFileName(string stem, int trackIndex)
    {
        return trackIndex == 0 ? $"{stem}.wav" : $"{stem}_track{trackIndex}.wav";
    }

    private static string TrackOutputPath(string outputDir, string stem, int trackIndex)
    {
        return Path.Combine(outputDir, GetTrackFileName(stem, trackIndex));
    }

    private static bool ConvertTrackToWav(Vid1AudioTrack track, string outputPath, string ffmpeg, out string error)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        var tempOggPath = Path.Combine(
            Path.GetTempPath(),
            "NeversoftMultitool",
            "Vid1Audio",
            $"{Guid.NewGuid():N}_{Path.GetFileNameWithoutExtension(outputPath)}.ogg");

        try
        {
            var tempDir = Path.GetDirectoryName(tempOggPath);
            if (!string.IsNullOrWhiteSpace(tempDir))
                Directory.CreateDirectory(tempDir);

            if (!TryWriteOggStream(track, tempOggPath, out error))
                return false;

            if (!TryDecodeOggToWav(ffmpeg, tempOggPath, outputPath, out error))
            {
                TryDeleteFile(outputPath);
                return false;
            }

            return true;
        }
        finally
        {
            TryDeleteFile(tempOggPath);
        }
    }

    internal static bool TryDecodeToPcm16(string inputPath, out Vid1PcmAudio? audio, out string error,
        int trackIndex = 0)
    {
        audio = null;

        var ffmpeg = SfdConverter.FindFfmpeg();
        if (ffmpeg == null)
        {
            error = "ffmpeg not found on PATH";
            return false;
        }

        if (!TryReadTrack(inputPath, trackIndex, out var track, out error))
            return false;

        var tempRoot = Path.Combine(Path.GetTempPath(), "NeversoftMultitool", "Vid1Audio");
        var tempStem = $"{Guid.NewGuid():N}_{Path.GetFileNameWithoutExtension(inputPath)}";
        var tempOggPath = Path.Combine(tempRoot, tempStem + ".ogg");
        var tempPcmPath = Path.Combine(tempRoot, tempStem + ".s16le");

        try
        {
            Directory.CreateDirectory(tempRoot);

            if (!TryWriteOggStream(track, tempOggPath, out error))
                return false;

            if (!TryDecodeOggToPcm16(ffmpeg, tempOggPath, tempPcmPath, out error))
                return false;

            var pcm = File.ReadAllBytes(tempPcmPath);
            audio = new Vid1PcmAudio(pcm, track.SampleRate, track.Channels, track.TotalSamples);
            error = "";
            return true;
        }
        finally
        {
            TryDeleteFile(tempOggPath);
            TryDeleteFile(tempPcmPath);
        }
    }

    internal static bool TryProbe(string inputPath, out Vid1AudioProbeResult? probe, out string error,
        int trackIndex = 0)
    {
        probe = null;
        if (!TryReadTrack(inputPath, trackIndex, out var track, out error, out var trackCount))
            return false;

        probe = CreateProbe(track, trackCount);
        error = "";
        return true;
    }

    internal static bool TryProbeTracks(string inputPath, out IReadOnlyList<Vid1AudioProbeResult> probes,
        out string error)
    {
        probes = [];

        if (!TryReadTracks(inputPath, out var tracks, out error))
            return false;

        probes = tracks.Select(track => CreateProbe(track, tracks.Count)).ToArray();
        error = "";
        return true;
    }

    private static Vid1AudioProbeResult CreateProbe(Vid1AudioTrack track, int trackCount)
    {
        return new Vid1AudioProbeResult(
            "VID1 Vorbis",
            track.SampleRate,
            track.Channels,
            track.TotalSamples,
            track.TrackIndex,
            trackCount);
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

    /// <summary>Reads a single audio track, also reporting the total track count found in the file.</summary>
    private static bool TryReadTrack(
        string inputPath,
        int trackIndex,
        out Vid1AudioTrack track,
        out string error,
        out int trackCount)
    {
        track = default!;
        trackCount = 0;

        if (!TryReadTracks(inputPath, out var tracks, out error))
            return false;

        trackCount = tracks.Count;
        if (trackIndex < 0 || trackIndex >= tracks.Count)
        {
            error = $"VID1 track {trackIndex} does not exist";
            return false;
        }

        track = tracks[trackIndex];
        return true;
    }

    private static bool TryReadTrack(string inputPath, int trackIndex, out Vid1AudioTrack track, out string error)
    {
        return TryReadTrack(inputPath, trackIndex, out track, out error, out _);
    }

    internal static bool TryReadTracks(string inputPath, out IReadOnlyList<Vid1AudioTrack> tracks, out string error)
    {
        tracks = [];

        try
        {
            var data = File.ReadAllBytes(inputPath);
            if (!TryParseVid1(data, out var parsedTracks, out error))
                return false;

            tracks = parsedTracks;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    ///     Parses every audio track carried in the VID1 file. The HEAD chunk holds one AUDH
    ///     header per track (e.g. one per dubbed language), and each FRAM chunk carries one
    ///     AUDD packet block per track in the same order.
    /// </summary>
    internal static bool TryParseVid1(byte[] data, out List<Vid1AudioTrack> tracks, out string error)
    {
        tracks = [];

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

        if (!TryCollectAudioHeaderChunks(data, headChunk.Offset + HeadChildOffset, headChunk.EndOffset,
                out var audhChunks, out error))
            return false;

        var trackHeaders = new List<Vid1AudioTrack>(audhChunks.Count);
        foreach (var audhChunk in audhChunks)
        {
            if (!TryParseAudioHeader(data, audhChunk, out var sampleRate, out var channels, out var totalSamples,
                    out var idPacket, out var setupPacket, out error))
                return false;

            trackHeaders.Add(new Vid1AudioTrack(
                trackHeaders.Count, sampleRate, channels, totalSamples, idPacket, setupPacket, []));
        }

        var trackPackets = trackHeaders.Select(static _ => new List<byte[]>()).ToList();
        var scanOffset = headChunk.EndOffset;

        while (scanOffset + 8 <= data.Length)
        {
            if (!TryReadChunk(data, scanOffset, data.Length, out var chunk, out error))
            {
                if (IsZeroPadding(data, scanOffset))
                    break;

                return false;
            }

            if (chunk.Tag == "FRAM" && !TryCollectFrameAudioPackets(data, chunk, trackPackets, out error))
                return false;

            scanOffset = chunk.EndOffset;
        }

        for (var i = 0; i < trackHeaders.Count; i++)
        {
            if (trackPackets[i].Count == 0)
            {
                error = trackHeaders.Count > 1
                    ? $"VID1 audio packets were not found for track {i}"
                    : "VID1 audio packets were not found";
                return false;
            }
        }

        tracks = trackHeaders
            .Select(header => header with { AudioPackets = trackPackets[header.TrackIndex] })
            .ToList();
        error = "";
        return true;
    }

    /// <summary>
    ///     Collects the sequential run of AUDH chunks in HEAD, starting from the first one found
    ///     (skipping VIDH and any other leading children) and stopping at the first non-AUDH
    ///     chunk, read failure, or trailing zero padding.
    /// </summary>
    private static bool TryCollectAudioHeaderChunks(
        byte[] data,
        int startOffset,
        int endOffset,
        out List<Vid1Chunk> chunks,
        out string error)
    {
        chunks = [];

        if (!TryFindChunk(data, startOffset, endOffset, "AUDH", out var first, out error))
            return false;

        chunks.Add(first);
        var offset = first.EndOffset;

        while (offset + 8 <= endOffset)
        {
            if (IsZeroPaddingRange(data, offset, endOffset))
                break;

            if (!TryReadChunk(data, offset, endOffset, out var candidate, out _))
                break;

            if (candidate.Tag != "AUDH")
                break;

            chunks.Add(candidate);
            offset = candidate.EndOffset;
        }

        error = "";
        return true;
    }

    private static bool TryCollectFrameAudioPackets(
        byte[] data,
        Vid1Chunk frameChunk,
        List<List<byte[]>> trackPackets,
        out string error)
    {
        error = "";
        var childOffset = FindFrameChildOffset(data, frameChunk);
        if (childOffset < 0)
            return true;

        // Each frame carries one AUDD block per audio track, in the same order as the
        // AUDH headers in HEAD (e.g. English, French, Italian, German, Spanish).
        var audioChunkIndex = 0;
        while (childOffset + 8 <= frameChunk.EndOffset)
        {
            if (!TryReadChunk(data, childOffset, frameChunk.EndOffset, out var chunk, out error))
                return false;

            if (chunk.Tag == "AUDD")
            {
                var trackIndex = Math.Min(audioChunkIndex, trackPackets.Count - 1);
                if (!TryCollectAudioBlockPackets(data, chunk, trackPackets[trackIndex], out error))
                    return false;

                audioChunkIndex++;
            }

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

        if (offset < 0 || offset > limit || limit - offset < 8 || offset > data.Length - 8)
        {
            error = "VID1 chunk header is truncated";
            return false;
        }

        var rawSize = ReadUInt32BigEndian(data, offset + 4);
        if (rawSize > int.MaxValue)
        {
            error = "VID1 chunk size is invalid";
            return false;
        }

        var size = (int)rawSize;
        if (size < 8)
        {
            error = "VID1 chunk size is invalid";
            return false;
        }

        if (size > limit - offset || size > data.Length - offset)
        {
            error = "VID1 chunk extends beyond the file";
            return false;
        }

        var endOffset = offset + size;
        chunk = new Vid1Chunk(
            Encoding.ASCII.GetString(data, offset, 4),
            offset,
            size,
            endOffset);
        error = "";
        return true;
    }

    private static bool TryWriteOggStream(Vid1AudioTrack track, string outputPath, out string error)
    {
        error = "";

        try
        {
            using var output = File.Create(outputPath);
            var packets = new List<byte[]>(3 + track.AudioPackets.Count)
            {
                track.IdPacket,
                BuildCommentPacket("NeversoftMultitool"),
                track.SetupPacket
            };
            packets.AddRange(track.AudioPackets);

            var serialNumber = 0x31564944u + (uint)track.TrackIndex;
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
                    if (track.TotalSamples > 0)
                    {
                        granulePosition = audioPacketIndex == track.AudioPackets.Count - 1
                            ? (ulong)track.TotalSamples
                            : (ulong)Math.Max(
                                1,
                                (int)Math.Round(
                                    (double)track.TotalSamples * (audioPacketIndex + 1) / track.AudioPackets.Count));
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

    private static bool IsZeroPaddingRange(byte[] data, int start, int end)
    {
        for (var i = start; i < end && i < data.Length; i++)
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

    /// <summary>One dubbed-language audio track carried inside a VID1 container.</summary>
    internal sealed record Vid1AudioTrack(
        int TrackIndex,
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

        public TimeSpan Duration
        {
            get
            {
                if (TotalSamples > 0 && SampleRate > 0)
                    return TimeSpan.FromSeconds((double)TotalSamples / SampleRate);

                var seconds = BytesPerSecond > 0 ? (double)Pcm16.Length / BytesPerSecond : 0;
                return TimeSpan.FromSeconds(seconds);
            }
        }
    }

    public sealed record Vid1AudioProbeResult(
        string CodecName,
        int SampleRate,
        int Channels,
        int TotalSamples,
        int TrackIndex,
        int TrackCount)
    {
        public double? DurationSeconds => SampleRate > 0 && TotalSamples > 0
            ? TotalSamples / (double)SampleRate
            : null;
    }
}
