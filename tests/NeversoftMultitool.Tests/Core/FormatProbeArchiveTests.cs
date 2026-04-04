using NeversoftMultitool.Core;

namespace NeversoftMultitool.Tests.Core;

public sealed class FormatProbeArchiveTests
{
    [Fact]
    public void ProbeArchive_WadFile_Supported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".wad", [0x00]);
        try
        {
            var result = FormatProbe.ProbeArchive(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeArchive_CompressedPre_Supported()
    {
        var data = new byte[8];
        BitConverter.GetBytes(0xABCD0002u).CopyTo(data, 4);
        var tempFile = FormatProbeTestHelper.CreateTempFile(".pre", data);
        try
        {
            var result = FormatProbe.ProbeArchive(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Contains("Compressed", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeArchive_PlainPre_Supported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".pre", new byte[8]);
        try
        {
            var result = FormatProbe.ProbeArchive(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("PRE Archive", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeArchive_BonV1_Supported()
    {
        var data = new byte[8];
        data[0] = (byte)'B';
        data[1] = (byte)'o';
        data[2] = (byte)'n';
        data[3] = 0;
        BitConverter.GetBytes(1u).CopyTo(data, 4);
        var tempFile = FormatProbeTestHelper.CreateTempFile(".bon", data);
        try
        {
            var result = FormatProbe.ProbeArchive(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Contains("BON", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeArchive_BonUnsupportedVersion_Unsupported()
    {
        var data = new byte[8];
        data[0] = (byte)'B';
        data[1] = (byte)'o';
        data[2] = (byte)'n';
        data[3] = 0;
        BitConverter.GetBytes(99u).CopyTo(data, 4);
        var tempFile = FormatProbeTestHelper.CreateTempFile(".bon", data);
        try
        {
            var result = FormatProbe.ProbeArchive(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Contains("99", result.UnsupportedReason!);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeArchive_PakRawDataFile_Unsupported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".pak", [0x00]);
        try
        {
            var result = FormatProbe.ProbeArchive(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Contains("not an archive", result.UnsupportedReason!);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}