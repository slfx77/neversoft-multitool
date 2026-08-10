using NeversoftMultitool.Core;
using NeversoftMultitool.Tests.Core.Formats.Video;

namespace NeversoftMultitool.Tests.Core;

public sealed class FormatProbeMediaTests
{
    [Fact]
    public void ProbeAudio_AdxFile_Supported()
    {
        var data = new byte[8];
        data[0] = 0x80;
        data[1] = 0x00;
        data[4] = 3;
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
        var data = new byte[8];
        data[0] = 0x80;
        data[1] = 0x00;
        data[4] = 7;
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
    public void ProbeVideo_StrValidSize_Supported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".str", new byte[2336]);
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
}
