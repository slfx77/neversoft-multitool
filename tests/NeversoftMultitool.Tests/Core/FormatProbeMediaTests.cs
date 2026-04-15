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
}
