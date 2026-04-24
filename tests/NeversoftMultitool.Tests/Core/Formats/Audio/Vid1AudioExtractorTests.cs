using System.Buffers.Binary;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.Video;
using NeversoftMultitool.Tests.Core;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class Vid1AudioExtractorTests(TestPaths paths)
{
    private string RepresentativeSampleFile =>
        paths.SampleBuildsDir is null ? string.Empty : Path.Combine(
            paths.SampleBuildsDir,
            "Tony Hawk's American Wasteland (2005-8-22, GC - Final)",
            "movies",
            "vid",
            "atvi.vid");

    private static string? FindRepoAtviVid()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            var candidate = Path.Combine(current, "TestOutput", "session7_atvi_src", "atvi.vid");
            if (File.Exists(candidate))
                return candidate;

            if (File.Exists(Path.Combine(current, "NeversoftMultitool.slnx")))
                break;

            var parent = Directory.GetParent(current);
            if (parent == null)
                break;

            current = parent.FullName;
        }

        return null;
    }

    private string? FindRepresentativeVid()
    {
        if (File.Exists(RepresentativeSampleFile))
            return RepresentativeSampleFile;

        return FindRepoAtviVid();
    }

    [Fact]
    public void TryReadPacketHeader_BitPackedHeader_DecodesPacketBounds()
    {
        var packet = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE };
        var data = Vid1TestBuilder.EncodeVid1Packet(packet);

        var success = Vid1AudioExtractor.TryReadPacketHeader(data, 0, data.Length, out var packetOffset, out var packetSize);

        Assert.True(success);
        Assert.Equal(packet.Length, packetSize);
        Assert.True(packetOffset > 0);
        Assert.Equal(packet, data.AsSpan(packetOffset, packetSize).ToArray());
    }

    [Fact]
    public void ProbeAudio_SyntheticVid1_IsSupported()
    {
        var vidPath = FormatProbeTestHelper.CreateTempFile(".vid", Vid1TestBuilder.CreateVid1());

        try
        {
            var result = FormatProbe.ProbeAudio(vidPath);

            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("VID1 Audio", result.FormatName);
        }
        finally
        {
            File.Delete(vidPath);
        }
    }

    [Fact]
    public void Probe_SyntheticVid1_ReturnsExpectedMetadata()
    {
        var vidPath = FormatProbeTestHelper.CreateTempFile(".vid", Vid1TestBuilder.CreateVid1(sampleRate: 32000, channels: 1, totalSamples: 2048));

        try
        {
            var probe = Vid1AudioExtractor.Probe(vidPath);

            Assert.NotNull(probe);
            Assert.Equal("VID1 Vorbis", probe!.CodecName);
            Assert.Equal(32000, probe.SampleRate);
            Assert.Equal(1, probe.Channels);
            Assert.Equal(2048, probe.TotalSamples);
        }
        finally
        {
            File.Delete(vidPath);
        }
    }

    [Fact]
    public void Probe_RepresentativeSample_ReturnsExpectedMetadata()
    {
        Assert.SkipWhen(!File.Exists(RepresentativeSampleFile), "Representative THAW GameCube VID sample not found");

        var probe = Vid1AudioExtractor.Probe(RepresentativeSampleFile);

        Assert.NotNull(probe);
        Assert.Equal("VID1 Vorbis", probe!.CodecName);
        Assert.Equal(44100, probe.SampleRate);
        Assert.Equal(2, probe.Channels);
        Assert.True(probe.TotalSamples > 0);
    }

    [Fact]
    public void ConvertToWav_RepresentativeSample_WritesWave()
    {
        Assert.SkipWhen(!File.Exists(RepresentativeSampleFile), "Representative THAW GameCube VID sample not found");
        Assert.SkipWhen(SfdConverter.FindFfmpeg() == null, "ffmpeg not found on PATH");

        var outputDir = FormatProbeTestHelper.CreateTempDirectory("vid_audio_extract");

        try
        {
            var result = Vid1AudioExtractor.ConvertToWav(RepresentativeSampleFile, outputDir);

            Assert.True(result.Success, result.ErrorMessage);

            var wavPath = Path.Combine(outputDir, "atvi.wav");
            Assert.True(File.Exists(wavPath));

            var wavBytes = File.ReadAllBytes(wavPath);
            Assert.True(wavBytes.AsSpan(0, 4).SequenceEqual("RIFF"u8));
            Assert.True(wavBytes.AsSpan(8, 4).SequenceEqual("WAVE"u8));
            Assert.Equal((short)2, BinaryPrimitives.ReadInt16LittleEndian(wavBytes.AsSpan(22, 2)));
            Assert.Equal(44100, BinaryPrimitives.ReadInt32LittleEndian(wavBytes.AsSpan(24, 4)));
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public void DecodeToPcm16_RepresentativeSample_MatchesVideoDuration()
    {
        var vidPath = FindRepresentativeVid();
        Assert.SkipWhen(vidPath == null, "Representative THAW GameCube VID sample not found");
        Assert.SkipWhen(SfdConverter.FindFfmpeg() == null, "ffmpeg not found on PATH");

        var video = Vid1VideoFile.Parse(vidPath!);

        var success = Vid1AudioExtractor.TryDecodeToPcm16(vidPath!, out var audio, out var error);

        Assert.True(success, error);
        Assert.NotNull(audio);
        Assert.NotEmpty(audio!.Pcm16);
        Assert.Equal(44100, audio.SampleRate);
        Assert.Equal(2, audio.Channels);
        Assert.True(audio.Duration > TimeSpan.Zero);
        Assert.InRange(
            Math.Abs((audio.Duration - video.Duration).TotalSeconds),
            0.0,
            0.5);
    }
}

internal static class Vid1TestBuilder
{
    public static byte[] CreateVid1(int sampleRate = 44100, int channels = 2, int totalSamples = 4096)
    {
        var idPacket = CreateVorbisIdentificationPacket(sampleRate, channels);
        var setupPacket = CreateVorbisSetupPacket();
        var headerBlob = EncodeVid1Packets(idPacket, setupPacket);

        var audhMetadata = new byte[0x24 + headerBlob.Length];
        "VAUD"u8.CopyTo(audhMetadata.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32BigEndian(audhMetadata.AsSpan(4, 4), checked((uint)sampleRate));
        audhMetadata[8] = checked((byte)channels);
        BinaryPrimitives.WriteUInt32BigEndian(audhMetadata.AsSpan(0x20, 4), checked((uint)totalSamples));
        headerBlob.CopyTo(audhMetadata.AsSpan(0x24));

        var audhPayload = new byte[4 + audhMetadata.Length];
        audhMetadata.CopyTo(audhPayload.AsSpan(4));
        var audhChunk = BuildChunk("AUDH", audhPayload);

        var audioPacket = new byte[] { 0x60, 0x11, 0x22, 0x33 };
        var auddPacketBlob = EncodeVid1Packets(audioPacket);
        var auddPayload = new byte[12 + auddPacketBlob.Length];
        BinaryPrimitives.WriteUInt32BigEndian(auddPayload.AsSpan(4, 4), checked((uint)(auddPacketBlob.Length + 6)));
        auddPacketBlob.CopyTo(auddPayload.AsSpan(12));
        var auddChunk = BuildChunk("AUDD", auddPayload);

        var framePayload = new byte[0x18 + auddChunk.Length];
        auddChunk.CopyTo(framePayload.AsSpan(0x18));
        var frameChunk = BuildChunk("FRAM", framePayload);

        var headPayload = new byte[4 + audhChunk.Length];
        audhChunk.CopyTo(headPayload.AsSpan(4));
        var headChunk = BuildChunk("HEAD", headPayload);

        var rootChunk = BuildChunk("VID1", new byte[0x18]);
        return [.. rootChunk, .. headChunk, .. frameChunk];
    }

    public static byte[] EncodeVid1Packet(byte[] packet)
    {
        return EncodeVid1Packets(packet);
    }

    private static byte[] EncodeVid1Packets(params byte[][] packets)
    {
        using var stream = new MemoryStream();
        foreach (var packet in packets)
        {
            var header = EncodePacketHeader(packet.Length);
            stream.Write(header);
            stream.Write(packet);
        }

        return stream.ToArray();
    }

    private static byte[] EncodePacketHeader(int packetSize)
    {
        var sizeBits = 0;
        while ((1 << (sizeBits + 1)) <= packetSize && sizeBits < 15)
            sizeBits++;

        var bits = new List<int>(4 + sizeBits + 1);
        for (var i = 0; i < 4; i++)
            bits.Add((sizeBits >> i) & 1);

        for (var i = 0; i < sizeBits + 1; i++)
            bits.Add((packetSize >> i) & 1);

        var byteCount = (bits.Count + 7) / 8;
        var bytes = new byte[byteCount];
        for (var i = 0; i < bits.Count; i++)
        {
            if (bits[i] == 0)
                continue;

            bytes[i / 8] |= (byte)(1 << (i % 8));
        }

        return bytes;
    }

    private static byte[] CreateVorbisIdentificationPacket(int sampleRate, int channels)
    {
        var packet = new byte[30];
        packet[0] = 0x01;
        "vorbis"u8.CopyTo(packet.AsSpan(1, 6));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(7, 4), 0);
        packet[11] = checked((byte)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), checked((uint)sampleRate));
        packet[28] = 0xB8;
        packet[29] = 0x01;
        return packet;
    }

    private static byte[] CreateVorbisSetupPacket()
    {
        var packet = new byte[16];
        packet[0] = 0x05;
        "vorbis"u8.CopyTo(packet.AsSpan(1, 6));
        for (var i = 7; i < packet.Length; i++)
            packet[i] = checked((byte)(i * 3));

        return packet;
    }

    private static byte[] BuildChunk(string tag, byte[] payload)
    {
        var chunk = new byte[8 + payload.Length];
        System.Text.Encoding.ASCII.GetBytes(tag).CopyTo(chunk, 0);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(4, 4), checked((uint)chunk.Length));
        payload.CopyTo(chunk.AsSpan(8));
        return chunk;
    }
}
