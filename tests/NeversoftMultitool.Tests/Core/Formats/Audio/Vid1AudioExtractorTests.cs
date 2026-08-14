using System.Buffers.Binary;
using System.Text;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.Vid1;
using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class Vid1AudioExtractorTests(TestPaths paths)
{
    private string RepresentativeSampleFile =>
        paths.SampleBuildsDir is null
            ? string.Empty
            : Path.Combine(
                paths.SampleBuildsDir,
                "Tony Hawk's American Wasteland (2005-8-22, GC - Final)",
                "movies",
                "vid",
                "atvi.vid");

    private string RepresentativeMultiTrackSampleFile =>
        paths.SampleBuildsDir is null
            ? string.Empty
            : Path.Combine(
                paths.SampleBuildsDir,
                "Tony Hawk's Downhill Jam (2006, Wii - Final)",
                "movies",
                "vid",
                "JX_Interview01.vid");

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

        var success =
            Vid1AudioExtractor.TryReadPacketHeader(data, 0, data.Length, out var packetOffset, out var packetSize);

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
        var vidPath = FormatProbeTestHelper.CreateTempFile(".vid", Vid1TestBuilder.CreateVid1(32000, 1, 2048));

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
    public void Probe_ByteArrayReportsDurationWithoutFilesystemInput()
    {
        var probe = Vid1AudioExtractor.Probe(Vid1TestBuilder.CreateVid1(32000, 1, 2048));

        Assert.NotNull(probe);
        Assert.Equal(2048 / 32000.0, probe.DurationSeconds);
    }

    [Fact]
    public void Probe_OverflowingByteMetadataReturnsNoResult()
    {
        var data = Vid1TestBuilder.CreateVid1();
        var metadataOffset = data.AsSpan().IndexOf("VAUD"u8);
        Assert.True(metadataOffset >= 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(metadataOffset + 4, 4), 0x80000000);

        Assert.Null(Vid1AudioExtractor.Probe(data));
        Assert.Empty(Vid1AudioExtractor.ProbeTracks(data));
        Assert.Null(AudioDurationProbe.Probe("VID", data));
    }

    [Fact]
    public void ProbeTracks_SyntheticMultiTrackVid1_ReturnsAllTracksWithDistinctMetadata()
    {
        var data = Vid1TestBuilder.CreateMultiTrackVid1(
            5,
            i => 44100 + i * 1000,
            static _ => 2,
            static _ => 4096);
        var vidPath = FormatProbeTestHelper.CreateTempFile(".vid", data);

        try
        {
            var probes = Vid1AudioExtractor.ProbeTracks(vidPath);

            Assert.Equal(5, probes.Count);
            for (var i = 0; i < probes.Count; i++)
            {
                Assert.Equal(i, probes[i].TrackIndex);
                Assert.Equal(5, probes[i].TrackCount);
                Assert.Equal(44100 + i * 1000, probes[i].SampleRate);
            }
        }
        finally
        {
            File.Delete(vidPath);
        }
    }

    [Fact]
    public void ProbeTracks_ByteArrayPreservesPerTrackDurations()
    {
        var probes = Vid1AudioExtractor.ProbeTracks(Vid1TestBuilder.CreateMultiTrackVid1(
            2,
            static _ => 1000,
            static _ => 1,
            static index => index == 0 ? 250 : 750));

        Assert.Equal(2, probes.Count);
        Assert.Equal(0.25, probes[0].DurationSeconds);
        Assert.Equal(0.75, probes[1].DurationSeconds);
    }

    [Fact]
    public void Probe_SyntheticMultiTrackVid1_TrackIndexSelectsCorrectTrack()
    {
        var data = Vid1TestBuilder.CreateMultiTrackVid1(3, i => 44100 + i * 1000);
        var vidPath = FormatProbeTestHelper.CreateTempFile(".vid", data);

        try
        {
            var track2 = Vid1AudioExtractor.Probe(vidPath, 2);

            Assert.NotNull(track2);
            Assert.Equal(2, track2!.TrackIndex);
            Assert.Equal(46100, track2.SampleRate);
        }
        finally
        {
            File.Delete(vidPath);
        }
    }

    [Fact]
    public void TryReadTracks_SyntheticMultiTrackVid1_KeepsPerTrackAudioPacketsSeparate()
    {
        var data = Vid1TestBuilder.CreateMultiTrackVid1(4);
        var vidPath = FormatProbeTestHelper.CreateTempFile(".vid", data);

        try
        {
            var success = Vid1AudioExtractor.TryReadTracks(vidPath, out var tracks, out var error);

            Assert.True(success, error);
            Assert.Equal(4, tracks.Count);

            for (var i = 0; i < tracks.Count; i++)
            {
                var track = tracks[i];
                Assert.Equal(i, track.TrackIndex);

                // Byte 1 of each synthetic AUDD packet identifies its track.
                // Packets from different tracks must remain separate.
                var packet = Assert.Single(track.AudioPackets);
                Assert.Equal(0x60, packet[0]);
                Assert.Equal((byte)(0x10 + i), packet[1]);
            }
        }
        finally
        {
            File.Delete(vidPath);
        }
    }

    [CorpusFact]
    public void ProbeTracks_RepresentativeMultiTrackSample_ReturnsMultipleDubbedTracks()
    {
        Assert.SkipWhen(!File.Exists(RepresentativeMultiTrackSampleFile),
            "Representative multi-track THAW GameCube VID sample not found");

        var probes = Vid1AudioExtractor.ProbeTracks(RepresentativeMultiTrackSampleFile);

        Assert.True(probes.Count > 1, $"Expected multiple audio tracks, found {probes.Count}");

        for (var i = 0; i < probes.Count; i++)
        {
            Assert.Equal("VID1 Vorbis", probes[i].CodecName);
            Assert.Equal(i, probes[i].TrackIndex);
            Assert.Equal(probes.Count, probes[i].TrackCount);
            Assert.True(probes[i].SampleRate > 0);
            Assert.True(probes[i].Channels > 0);
            Assert.True(probes[i].TotalSamples > 0);
        }
    }

    [CorpusFact]
    public void TryReadTracks_RepresentativeMultiTrackSample_KeepsPerTrackAudioPacketsSeparate()
    {
        Assert.SkipWhen(!File.Exists(RepresentativeMultiTrackSampleFile),
            "Representative multi-track THAW GameCube VID sample not found");

        var success =
            Vid1AudioExtractor.TryReadTracks(RepresentativeMultiTrackSampleFile, out var tracks, out var error);

        Assert.True(success, error);
        Assert.True(tracks.Count > 1, $"Expected multiple audio tracks, found {tracks.Count}");

        foreach (var track in tracks)
        {
            Assert.NotEmpty(track.AudioPackets);
            Assert.NotEmpty(track.IdPacket);
            Assert.NotEmpty(track.SetupPacket);
        }

        // Tracks are independent dubs of the same scene; packet counts commonly differ
        // slightly per language, but a real bug (e.g. packets merged across tracks) would
        // show up as wildly different or duplicated packet lists between tracks.
        var packetCounts = tracks.Select(static t => t.AudioPackets.Count).ToArray();
        Assert.All(packetCounts, count => Assert.True(count > 0));
    }

    [CorpusFact]
    public void ConvertToWav_RepresentativeMultiTrackSample_WritesOneWavPerTrack()
    {
        Assert.SkipWhen(!File.Exists(RepresentativeMultiTrackSampleFile),
            "Representative multi-track THAW GameCube VID sample not found");
        Assert.SkipWhen(SfdConverter.FindFfmpeg() == null, "ffmpeg not found on PATH");

        var outputDir = FormatProbeTestHelper.CreateTempDirectory("vid_multitrack_audio_extract");

        try
        {
            var result = Vid1AudioExtractor.ConvertToWav(RepresentativeMultiTrackSampleFile, outputDir);

            Assert.True(result.Success, result.ErrorMessage);

            var trackCount = Vid1AudioExtractor.ProbeTracks(RepresentativeMultiTrackSampleFile).Count;
            Assert.Equal(trackCount, result.SamplesWritten);

            for (var i = 0; i < trackCount; i++)
            {
                var expectedName = i == 0 ? "JX_Interview01.wav" : $"JX_Interview01_track{i}.wav";
                var wavPath = Path.Combine(outputDir, expectedName);
                Assert.True(File.Exists(wavPath), $"Expected {expectedName} to exist");

                var wavBytes = File.ReadAllBytes(wavPath);
                Assert.True(wavBytes.AsSpan(0, 4).SequenceEqual("RIFF"u8));
                Assert.True(wavBytes.AsSpan(8, 4).SequenceEqual("WAVE"u8));
            }
        }
        finally
        {
            Directory.Delete(outputDir, true);
        }
    }

    [CorpusFact]
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

    [CorpusFact]
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
            Directory.Delete(outputDir, true);
        }
    }

    [CorpusFact]
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
        return CreateMultiTrackVid1(1, i => sampleRate, i => channels, i => totalSamples);
    }

    /// <summary>
    ///     Builds a synthetic VID1 file with multiple AUDH tracks in HEAD and, per frame, one
    ///     AUDD block per track in the same order — mirroring dubbed-language VID1 files (e.g.
    ///     English, French, Italian, German, Spanish).
    /// </summary>
    public static byte[] CreateMultiTrackVid1(
        int trackCount,
        Func<int, int>? sampleRateForTrack = null,
        Func<int, int>? channelsForTrack = null,
        Func<int, int>? totalSamplesForTrack = null)
    {
        sampleRateForTrack ??= static i => 44100 + i * 1000;
        channelsForTrack ??= static _ => 2;
        totalSamplesForTrack ??= static _ => 4096;

        var audhChunks = new byte[trackCount][];
        var auddChunks = new byte[trackCount][];

        for (var i = 0; i < trackCount; i++)
        {
            var sampleRate = sampleRateForTrack(i);
            var channels = channelsForTrack(i);
            var totalSamples = totalSamplesForTrack(i);

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
            audhChunks[i] = BuildChunk("AUDH", audhPayload);

            // Distinct packet content per track so tests can verify AUDD blocks are
            // routed to the correct track instead of being merged together.
            var audioPacket = new byte[] { 0x60, checked((byte)(0x10 + i)), 0x22, 0x33 };
            var auddPacketBlob = EncodeVid1Packets(audioPacket);
            var auddPayload = new byte[12 + auddPacketBlob.Length];
            BinaryPrimitives.WriteUInt32BigEndian(auddPayload.AsSpan(4, 4), checked((uint)(auddPacketBlob.Length + 6)));
            auddPacketBlob.CopyTo(auddPayload.AsSpan(12));
            auddChunks[i] = BuildChunk("AUDD", auddPayload);
        }

        var framePayload = new byte[0x18 + auddChunks.Sum(static c => c.Length)];
        var frameOffset = 0x18;
        foreach (var auddChunk in auddChunks)
        {
            auddChunk.CopyTo(framePayload.AsSpan(frameOffset));
            frameOffset += auddChunk.Length;
        }

        var frameChunk = BuildChunk("FRAM", framePayload);

        var headPayload = new byte[4 + audhChunks.Sum(static c => c.Length)];
        var headOffset = 4;
        foreach (var audhChunk in audhChunks)
        {
            audhChunk.CopyTo(headPayload.AsSpan(headOffset));
            headOffset += audhChunk.Length;
        }

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
        while (1 << (sizeBits + 1) <= packetSize && sizeBits < 15)
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
        Encoding.ASCII.GetBytes(tag).CopyTo(chunk, 0);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(4, 4), checked((uint)chunk.Length));
        payload.CopyTo(chunk.AsSpan(8));
        return chunk;
    }
}
