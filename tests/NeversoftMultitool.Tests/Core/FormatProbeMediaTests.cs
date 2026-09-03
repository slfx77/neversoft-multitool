using System.Buffers.Binary;
using NeversoftMultitool.Core;
using NeversoftMultitool.Tests.Core.Formats.Video;

namespace NeversoftMultitool.Tests.Core;

public sealed class FormatProbeMediaTests
{
    [Fact]
    public void ProbeAudio_AdxFile_Supported()
    {
        var data = BuildAdxProbe(3);
        var tempFile = FormatProbeTestHelper.CreateTempFile(".adx", data);
        try
        {
            var result = FormatProbe.ProbeAudio(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeAudio_AdxUnsupportedEncoding_Unsupported()
    {
        var data = BuildAdxProbe(7);
        var tempFile = FormatProbeTestHelper.CreateTempFile(".adx", data);
        try
        {
            var result = FormatProbe.ProbeAudio(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Contains("encoding", result.UnsupportedReason!);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeAudio_VagFile_Supported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".vag", [0x00]);
        try
        {
            var result = FormatProbe.ProbeAudio(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeAudio_XaFile_Supported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".xa", [0x00]);
        try
        {
            var result = FormatProbe.ProbeAudio(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeAudio_Thug2PcSndFile_Supported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".snd", BuildSndProbe());
        try
        {
            var result = FormatProbe.ProbeAudio(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("THUG2 PC Sound", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeAudio_TruncatedAdxHeader_Unsupported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".adx", BuildAdxProbe(3)[..17]);
        try
        {
            var result = FormatProbe.ProbeAudio(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Contains("too small", result.UnsupportedReason!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeAudio_OrdinaryPcmNamedSnd_Unsupported()
    {
        var data = BuildSndProbe();
        // Replace THUG2's decoded-byte count with a normal PCM byte rate.
        BitConverter.GetBytes(44100 * 2).CopyTo(data, 28);
        var tempFile = FormatProbeTestHelper.CreateTempFile(".snd", data);
        try
        {
            var result = FormatProbe.ProbeAudio(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeVideo_SfdFile_Supported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".sfd", [0x00]);
        try
        {
            var result = FormatProbe.ProbeVideo(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData(".SFD", "SFD Video")]
    [InlineData(".PsS", "PSS Video")]
    [InlineData(".BiK", "BIK Video")]
    public void ProbeVideo_SimpleExtensions_AreCaseInsensitive(string extension, string expectedFormat)
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(extension, [0x00]);
        try
        {
            var result = FormatProbe.ProbeVideo(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal(expectedFormat, result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData(".tgr")]
    [InlineData(".TgR")]
    public void ProbeVideo_Thps4TgrBink_IsContentGated(string extension)
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(extension, "BIKi"u8.ToArray());
        try
        {
            var result = FormatProbe.ProbeVideo(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("BIK Video (THPS4 PC)", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeVideo_NonBinkTgr_IsUnsupported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".tgr", "TGR data"u8.ToArray());
        try
        {
            var result = FormatProbe.ProbeVideo(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Contains("Bink", result.UnsupportedReason!);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData(".smo")]
    [InlineData(".SmO")]
    public void ProbeVideo_Thps4PcSmo_IsStrictlyContentGated(string extension)
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(extension, BuildSmoProbe());
        try
        {
            var result = FormatProbe.ProbeVideo(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("BIK SMO Soundtrack (THPS4 PC)", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeVideo_GenericBinkNamedSmo_IsUnsupported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".smo", "BIKi"u8.ToArray());
        try
        {
            var result = FormatProbe.ProbeVideo(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Contains("BIKi soundtrack", result.UnsupportedReason!);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeVideo_StrInvalidSize_Unsupported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".str", new byte[100]);
        try
        {
            var result = FormatProbe.ProbeVideo(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Contains("STR", result.UnsupportedReason!);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeVideo_StrSectorAlignedDataWithoutVideoMarker_Unsupported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".str", new byte[2336]);
        try
        {
            var result = FormatProbe.ProbeVideo(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Contains("video frame", result.UnsupportedReason!);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeVideo_StrCompleteSingleSectorVideo_Supported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".str", BuildStrSector());
        try
        {
            var result = FormatProbe.ProbeVideo(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("STR Video", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeVideo_StrMixedCaseExtension_Supported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".StR", BuildStrSector());
        try
        {
            var result = FormatProbe.ProbeVideo(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("STR Video", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeVideo_Vid1File_Supported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".vid", Vid1VideoTestBuilder.CreateVideoVid1());
        try
        {
            var result = FormatProbe.ProbeVideo(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("VID1 Video", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeVideo_StrIncompleteFirstFrame_Unsupported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".str", BuildStrSector(chunkCount: 2));
        try
        {
            var result = FormatProbe.ProbeVideo(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Contains("video frame", result.UnsupportedReason!);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeVideo_RiffCdxaHeaderWithoutSector_Unsupported()
    {
        var data = new byte[44];
        "RIFF"u8.CopyTo(data);
        "CDXA"u8.CopyTo(data.AsSpan(8));
        var tempFile = FormatProbeTestHelper.CreateTempFile(".str", data);
        try
        {
            var result = FormatProbe.ProbeVideo(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Contains("video frame", result.UnsupportedReason!);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeVideo_RiffCdxaCompleteVideo_PreservesFormatName()
    {
        var data = new byte[44 + 2352];
        "RIFF"u8.CopyTo(data);
        "CDXA"u8.CopyTo(data.AsSpan(8));
        BuildStrSector().CopyTo(data, 44 + 16);
        var tempFile = FormatProbeTestHelper.CreateTempFile(".str", data);
        try
        {
            var result = FormatProbe.ProbeVideo(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("STR Video (RIFF/CDXA)", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeVideo_Vid1MixedCaseExtension_Supported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".ViD", Vid1VideoTestBuilder.CreateVideoVid1());
        try
        {
            var result = FormatProbe.ProbeVideo(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("VID1 Video", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    private static byte[] BuildAdxProbe(byte encoding)
    {
        var data = new byte[36];
        BinaryPrimitives.WriteUInt16BigEndian(data, 0x8000);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), 14);
        data[4] = encoding;
        data[5] = 18;
        data[6] = 4;
        data[7] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 44_100);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), 32);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(16), 500);
        return data;
    }

    private static byte[] BuildSndProbe()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(44);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(44100);
        writer.Write(8); // two payload bytes decode to four int16 samples
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(2);
        writer.Write((byte)0x01);
        writer.Write((byte)0x24);
        return stream.ToArray();
    }

    private static byte[] BuildSmoProbe()
    {
        const int length = 128;
        const uint frameCount = 4;
        var data = new byte[length];
        "BIKi"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), length - 8);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), frameCount);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), frameCount);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), 15);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(44), 4096);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(48), 48_000);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(50), 0x7000);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(56), 77);
        return data;
    }

    private static byte[] BuildStrSector(ushort chunkCount = 1)
    {
        const int sectorSize = 2336;
        const int videoHeaderOffset = 8;
        var data = new byte[sectorSize];
        data[2] = 0x48;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(videoHeaderOffset), 0x0160);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(videoHeaderOffset + 2), 0x8001);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(videoHeaderOffset + 4), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(videoHeaderOffset + 6), chunkCount);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(videoHeaderOffset + 8), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(videoHeaderOffset + 12), 2016);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(videoHeaderOffset + 16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(videoHeaderOffset + 18), 16);
        return data;
    }
}
