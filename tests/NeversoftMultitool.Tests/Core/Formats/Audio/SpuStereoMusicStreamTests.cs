using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

/// <summary>
///     Neversoft PS2 music streams: headerless SPU-ADPCM, stereo as alternating
///     0x18000-byte L/R chunks at 48 kHz (THUG source Gel/Music/Ngps ground
///     truth — see SpuStereoMusicStream). These tests pin the detection gate,
///     the stereo decode path, and that headered/mono inputs stay untouched.
/// </summary>
public sealed class SpuStereoMusicStreamTests
{
    private const int ChunkSize = SpuStereoMusicStream.ChunkSize;
    private const int BlockSize = 16;

    [Fact]
    public void IsStereoMusic_ChunkInterleavedStereo_Detected()
    {
        Assert.True(SpuStereoMusicStream.IsStereoMusic(BuildStereoFixture(2)));
    }

    [Fact]
    public void IsStereoMusic_TooSmallForTwoChunkPairs_NotDetected()
    {
        // Three chunks < the 2-pair minimum — voice-stream sized files never
        // reach the content check.
        var fixture = BuildStereoFixture(2).AsSpan(0, ChunkSize * 3).ToArray();
        Assert.False(SpuStereoMusicStream.IsStereoMusic(fixture));
    }

    [Fact]
    public void IsStereoMusic_FlatEnvelopePseudoChannels_NotDetected()
    {
        // A mono stream whose loudness only changes per whole chunk produces
        // constant per-pseudo-channel envelopes — zero variance must not read
        // as correlation.
        var data = new byte[ChunkSize * 4];
        for (var chunk = 0; chunk < 4; chunk++)
        {
            var amplitude = chunk % 2 == 0 ? (byte)0x77 : (byte)0x11;
            FillBlocks(data.AsSpan(chunk * ChunkSize, ChunkSize), _ => amplitude);
        }

        Assert.False(SpuStereoMusicStream.IsStereoMusic(data));
    }

    [Fact]
    public void ConvertToWav_StereoMusicStream_Writes48kStereoWav()
    {
        var fixture = BuildStereoFixture(2);
        var outputDir = FormatProbeTestHelper.CreateTempDirectory("vag_stereo");

        try
        {
            var result = VagDecoder.ConvertToWav(fixture, "music_test", outputDir);

            Assert.True(result.Success, result.ErrorMessage);

            var wavBytes = File.ReadAllBytes(Path.Combine(outputDir, "music_test.wav"));
            Assert.True(wavBytes.AsSpan(0, 4).SequenceEqual("RIFF"u8));
            Assert.Equal((short)2, BinaryPrimitives.ReadInt16LittleEndian(wavBytes.AsSpan(22, 2)));
            Assert.Equal(48000, BinaryPrimitives.ReadInt32LittleEndian(wavBytes.AsSpan(24, 4)));

            // 2 chunks per channel = (2 * 0x18000 / 16) blocks * 28 samples
            var expectedFrames = 2 * (ChunkSize / BlockSize) * SpuAdpcm.SamplesPerBlock;
            var dataBytes = BinaryPrimitives.ReadInt32LittleEndian(wavBytes.AsSpan(40, 4));
            Assert.Equal(expectedFrames * 2 * sizeof(short), dataBytes);
        }
        finally
        {
            Directory.Delete(outputDir, true);
        }
    }

    [Fact]
    public void Probe_StereoMusicStream_Reports48kStereo()
    {
        var probe = VagDecoder.Probe(BuildStereoFixture(2));

        Assert.NotNull(probe);
        Assert.Equal(48000, probe!.SampleRate);
        Assert.Equal(2, probe.Channels);
        Assert.False(probe.HasHeader);
    }

    [Fact]
    public void EstimateDuration_CompleteChunkPairs_ReportsDecodedFrameDuration()
    {
        Assert.Equal(7.168, SpuStereoMusicStream.EstimateDuration(4L * ChunkSize), 12);
    }

    [Fact]
    public void EstimateDuration_FinalLeftOnlyChunk_UsesShorterDecodedChannel()
    {
        Assert.Equal(7.168, SpuStereoMusicStream.EstimateDuration(5L * ChunkSize), 12);
    }

    [Theory]
    [InlineData(15, "left")]
    [InlineData(ChunkSize + 15, "right")]
    public void DecodeInterleaved_PartialChannelBlockWithoutEndMarker_IsRejected(
        int appendedBytes,
        string channelName)
    {
        var data = AppendBytes(BuildStereoFixture(2), appendedBytes, 0xA5);

        var exception = Assert.Throws<InvalidDataException>(
            () => SpuStereoMusicStream.DecodeInterleaved(data));

        Assert.Equal(
            $"Stereo music {channelName} channel has a 15-byte partial " +
            "SPU-ADPCM block without an earlier end marker.",
            exception.Message);
    }

    [Theory]
    [InlineData(15, 3 * ChunkSize - 15)]
    [InlineData(ChunkSize + 15, 4 * ChunkSize - 15)]
    public void DecodeInterleaved_PartialPaddingAfterChannelEndMarker_IsIgnored(
        int appendedBytes,
        int endFlagOffset)
    {
        var baseline = BuildStereoFixture(2);
        baseline[endFlagOffset] = SpuAdpcm.FlagEnd;
        var expected = SpuStereoMusicStream.DecodeInterleaved(baseline);
        var padded = AppendBytes(baseline, appendedBytes, 0xA5);

        var actual = SpuStereoMusicStream.DecodeInterleaved(padded);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DecodeInterleaved_AlignedFinalLeftOnlyChunk_RemainsAccepted()
    {
        var baseline = BuildStereoFixture(2);
        var withLeftTail = AppendBytes(baseline, ChunkSize, 0);
        FillBlocks(withLeftTail.AsSpan(baseline.Length), _ => 0x22);

        var actual = SpuStereoMusicStream.DecodeInterleaved(withLeftTail);

        Assert.Equal(SpuStereoMusicStream.DecodeInterleaved(baseline), actual);
    }

    [Fact]
    public void ConvertToWav_PartialChannelBlock_FailsWithoutWritingOutput()
    {
        var data = AppendBytes(BuildStereoFixture(2), 15, 0xA5);
        var outputDir = FormatProbeTestHelper.CreateTempDirectory("vag_stereo_partial");

        try
        {
            var result = VagDecoder.ConvertToWav(data, "partial_music", outputDir);

            Assert.False(result.Success);
            Assert.Equal(
                "Stereo music left channel has a 15-byte partial SPU-ADPCM block " +
                "without an earlier end marker.",
                result.ErrorMessage);
            Assert.False(File.Exists(Path.Combine(outputDir, "partial_music.wav")));
        }
        finally
        {
            Directory.Delete(outputDir, true);
        }
    }

    [Fact]
    public void ConvertToWav_HeaderedVagp_StaysMonoAtHeaderRate()
    {
        var data = new byte[48 + BlockSize * 4];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), 0x56414770); // "VAGp"
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(12, 4), BlockSize * 4);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(16, 4), 32000);
        FillBlocks(data.AsSpan(48), _ => 0x33);

        var outputDir = FormatProbeTestHelper.CreateTempDirectory("vag_mono");
        try
        {
            var result = VagDecoder.ConvertToWav(data, "sfx_test", outputDir);

            Assert.True(result.Success, result.ErrorMessage);

            var wavBytes = File.ReadAllBytes(Path.Combine(outputDir, "sfx_test.wav"));
            Assert.Equal((short)1, BinaryPrimitives.ReadInt16LittleEndian(wavBytes.AsSpan(22, 2)));
            Assert.Equal(32000, BinaryPrimitives.ReadInt32LittleEndian(wavBytes.AsSpan(24, 4)));
        }
        finally
        {
            Directory.Delete(outputDir, true);
        }
    }

    /// <summary>
    ///     Alternating L/R chunks where both channels carry the same
    ///     amplitude-modulated pattern (loud/quiet every 100 blocks) — the
    ///     envelopes vary over time and correlate across channels, like real
    ///     stereo music.
    /// </summary>
    private static byte[] BuildStereoFixture(int pairs)
    {
        var channelChunk = new byte[ChunkSize];
        FillBlocks(channelChunk, blockIndex => blockIndex / 100 % 2 == 0 ? (byte)0x77 : (byte)0x11);

        var data = new byte[pairs * ChunkSize * 2];
        for (var pair = 0; pair < pairs; pair++)
        {
            channelChunk.CopyTo(data.AsSpan(pair * ChunkSize * 2));
            channelChunk.CopyTo(data.AsSpan(pair * ChunkSize * 2 + ChunkSize));
        }

        return data;
    }

    private static byte[] AppendBytes(byte[] source, int count, byte value)
    {
        var result = new byte[source.Length + count];
        source.CopyTo(result, 0);
        result.AsSpan(source.Length).Fill(value);
        return result;
    }

    /// <summary>Fills SPU-ADPCM blocks (filter 0, shift 3) with a per-block nibble byte.</summary>
    private static void FillBlocks(Span<byte> region, Func<int, byte> nibbleByteForBlock)
    {
        for (var block = 0; block < region.Length / BlockSize; block++)
        {
            var span = region.Slice(block * BlockSize, BlockSize);
            span[0] = 0x03; // shift 3, filter 0
            span[1] = 0x00; // no flags
            span[2..].Fill(nibbleByteForBlock(block));
        }
    }
}
