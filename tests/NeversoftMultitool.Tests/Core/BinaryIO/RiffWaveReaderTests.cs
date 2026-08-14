using System.Text;
using NeversoftMultitool.Core.BinaryIO;

namespace NeversoftMultitool.Tests.Core.BinaryIO;

public class RiffWaveReaderTests
{
    /// <summary>
    ///     Builds a RIFF/WAVE buffer from a chunk list. <paramref name="riffSize" />
    ///     is written verbatim so tests can reproduce the corpus's wrong values.
    /// </summary>
    private static byte[] BuildWave(IEnumerable<(string Id, byte[] Payload)> chunks, uint? riffSize = null)
    {
        var body = new MemoryStream();
        foreach (var (id, payload) in chunks)
        {
            body.Write(Encoding.ASCII.GetBytes(id));
            body.Write(BitConverter.GetBytes((uint)payload.Length));
            body.Write(payload);
            if (payload.Length % 2 == 1)
                body.WriteByte(0); // RIFF word pad
        }

        var bodyBytes = body.ToArray();
        var file = new MemoryStream();
        file.Write("RIFF"u8);
        file.Write(BitConverter.GetBytes(riffSize ?? (uint)(4 + bodyBytes.Length)));
        file.Write("WAVE"u8);
        file.Write(bodyBytes);
        return file.ToArray();
    }

    private static byte[] Fmt(int tag, int channels, int rate, int blockAlign, int bits, int? samplesPerBlock = null)
    {
        var payload = new MemoryStream();
        payload.Write(BitConverter.GetBytes((ushort)tag));
        payload.Write(BitConverter.GetBytes((ushort)channels));
        payload.Write(BitConverter.GetBytes((uint)rate));
        payload.Write(BitConverter.GetBytes((uint)(rate * blockAlign)));
        payload.Write(BitConverter.GetBytes((ushort)blockAlign));
        payload.Write(BitConverter.GetBytes((ushort)bits));
        if (samplesPerBlock is { } spb)
        {
            payload.Write(BitConverter.GetBytes((ushort)2)); // cbSize
            payload.Write(BitConverter.GetBytes((ushort)spb));
        }

        return payload.ToArray();
    }

    [Fact]
    public void TryRead_MinimalPcmWave_ReturnsFormatAndData()
    {
        var wave = BuildWave([("fmt ", Fmt(1, 1, 44100, 2, 16)), ("data", new byte[64])]);

        Assert.True(RiffWaveReader.TryRead(wave, out var info));
        Assert.Equal(1, info.FormatTag);
        Assert.Equal(1, info.Channels);
        Assert.Equal(44100, info.SampleRate);
        Assert.Equal(64, info.DataLength);
        Assert.Equal(0, info.SamplesPerBlock);
    }

    [Fact]
    public void TryRead_FmtWithExtension_ReadsSamplesPerBlock()
    {
        var wave = BuildWave([("fmt ", Fmt(0x0069, 1, 44100, 36, 4, 64)), ("data", new byte[36])]);

        Assert.True(RiffWaveReader.TryRead(wave, out var info));
        Assert.Equal(0x0069, info.FormatTag);
        Assert.Equal(36, info.BlockAlign);
        Assert.Equal(64, info.SamplesPerBlock);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TryRead_FmtExtensionTooShort_DoesNotReadSamplesPerBlock(int extensionSize)
    {
        var format = Fmt(0x0069, 1, 44100, 36, 4, 64);
        BitConverter.GetBytes((ushort)extensionSize).CopyTo(format, 16);
        var wave = BuildWave([("fmt ", format), ("data", new byte[36])]);

        Assert.True(RiffWaveReader.TryRead(wave, out var info));
        Assert.Equal(0x0069, info.FormatTag);
        Assert.Equal(36, info.DataLength);
        Assert.Equal(0, info.SamplesPerBlock);
    }

    [Fact]
    public void TryRead_BroadcastWaveLayout_WalksPastChunksBeforeFmt()
    {
        // The real 172-file layout: bext, fmt, minf, elmo, data.
        var wave = BuildWave(
        [
            ("bext", new byte[602]),
            ("fmt ", Fmt(0x0069, 1, 44100, 36, 4, 64)),
            ("minf", new byte[16]),
            ("elmo", new byte[338]),
            ("data", new byte[72])
        ]);

        Assert.True(RiffWaveReader.TryRead(wave, out var info));
        Assert.Equal(0x0069, info.FormatTag);
        Assert.Equal(72, info.DataLength);
    }

    [Fact]
    public void TryRead_JunkBetweenFmtAndData_IsSkipped()
    {
        var wave = BuildWave(
        [
            ("fmt ", Fmt(0x0069, 1, 44100, 36, 4, 64)),
            ("JUNK", new byte[450]),
            ("data", new byte[36])
        ]);

        Assert.True(RiffWaveReader.TryRead(wave, out var info));
        Assert.Equal(36, info.DataLength);
    }

    [Fact]
    public void TryRead_OddSizedChunk_AppliesWordPadding()
    {
        var wave = BuildWave(
        [
            ("fmt ", Fmt(1, 1, 22050, 2, 16)),
            ("LIST", new byte[7]), // odd -> one pad byte
            ("data", new byte[8])
        ]);

        Assert.True(RiffWaveReader.TryRead(wave, out var info));
        Assert.Equal(8, info.DataLength);
    }

    [Fact]
    public void TryRead_RiffSizeFourTimesTheFileLength_StillFindsData()
    {
        // Every THUG2 PC .snd declares roughly 4x its real length, because the
        // field holds the DECODED size. The walk must ignore it.
        var wave = BuildWave([("fmt ", Fmt(1, 1, 44100, 2, 16)), ("data", new byte[100])], riffSize: 999999);

        Assert.True(RiffWaveReader.TryRead(wave, out var info));
        Assert.Equal(100, info.DataLength);
    }

    [Fact]
    public void TryRead_DataSizeBeyondBuffer_ClampsToWhatExists()
    {
        var wave = BuildWave([("fmt ", Fmt(1, 1, 44100, 2, 16)), ("data", new byte[40])]);
        // Overstate the data chunk size by 1 KB.
        var dataSizeOffset = wave.Length - 40 - 4;
        BitConverter.GetBytes(1064u).CopyTo(wave, dataSizeOffset);

        Assert.True(RiffWaveReader.TryRead(wave, out var info));
        Assert.Equal(40, info.DataLength);
    }

    [Fact]
    public void TryReadHeader_DataBeyondProbePrefix_MatchesFullBufferLength()
    {
        var wave = BuildWave([("fmt ", Fmt(1, 1, 44100, 2, 16)), ("data", new byte[9_000])]);
        var path = Path.Combine(
            Path.GetTempPath(), $"nmt_riff_probe_{Guid.NewGuid():N}.wav");

        try
        {
            File.WriteAllBytes(path, wave);

            Assert.True(RiffWaveReader.TryRead(wave, out var fullInfo));
            Assert.True(RiffWaveReader.TryReadHeader(path, out var headerInfo));

            Assert.Equal(44, headerInfo.DataOffset);
            Assert.Equal(9_000, headerInfo.DataLength);
            Assert.Equal(fullInfo.DataLength, headerInfo.DataLength);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryRead_ChunkSizeOverflow_ReturnsFalse()
    {
        var wave = BuildWave([("fmt ", Fmt(1, 1, 44100, 2, 16)), ("data", new byte[8])]);
        // Corrupt the fmt chunk size so the walk would overrun.
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(wave, 16);

        Assert.False(RiffWaveReader.TryRead(wave, out _));
    }

    [Fact]
    public void TryRead_DataWithoutFmt_ReturnsFalse()
    {
        var wave = BuildWave([("data", new byte[16])]);

        Assert.False(RiffWaveReader.TryRead(wave, out _));
    }

    [Fact]
    public void IsRiffWave_RejectsNonWaveContainers()
    {
        Assert.False(RiffWaveReader.IsRiffWave("RIFFxxxxCDXA"u8));
        Assert.False(RiffWaveReader.IsRiffWave(new byte[64]));
        Assert.False(RiffWaveReader.IsRiffWave([1, 2, 3]));
        Assert.True(RiffWaveReader.IsRiffWave("RIFF\0\0\0\0WAVE"u8));
    }
}
