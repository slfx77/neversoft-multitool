using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Tests.Core;

public sealed class FormatProbeArchiveTests
{
    [Theory]
    [InlineData(".pre")]
    [InlineData(".prd")]
    [InlineData(".prf")]
    [InlineData(".prg")]
    public void ProbeArchive_MissingPreFile_IsUnsupported(string extension)
    {
        var missingPath = Path.Combine(
            Path.GetTempPath(),
            $"{Guid.NewGuid():N}{extension}");

        var result = FormatProbe.ProbeArchive(missingPath);

        Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
        Assert.Contains("Failed to read file header", result.UnsupportedReason);
    }

    [Fact]
    public void ProbeArchive_MissingPrxFile_ReportsHeaderReadFailure()
    {
        var missingPath = Path.Combine(
            Path.GetTempPath(),
            $"{Guid.NewGuid():N}.prx");

        var result = FormatProbe.ProbeArchive(missingPath);

        Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
        Assert.Equal("Unknown", result.FormatName);
        Assert.Equal("Failed to read file header", result.UnsupportedReason);
    }

    [Theory]
    [InlineData(0xABCD0002u, ".prx", 0)]
    [InlineData(0xABCD0003u, ".PrX", 5)]
    public void ProbeArchive_ValidPrxHeader_IsSupported(
        uint version,
        string extension,
        int trailingByteCount)
    {
        var data = CreateCompressedPreHeader(version, trailingByteCount);
        var tempFile = FormatProbeTestHelper.CreateTempFile(extension, data);
        try
        {
            var result = FormatProbe.ProbeArchive(tempFile);

            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("Compressed PRE", result.FormatName);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(11)]
    public void ProbeArchive_TruncatedPrxHeader_IsUnsupported(int byteCount)
    {
        var data = new byte[byteCount];
        if (byteCount >= 8)
            BitConverter.GetBytes(0xABCD0002u).CopyTo(data, 4);

        var tempFile = FormatProbeTestHelper.CreateTempFile(".prx", data);
        try
        {
            var result = FormatProbe.ProbeArchive(tempFile);

            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Equal("Unknown", result.FormatName);
            Assert.Equal("File too small", result.UnsupportedReason);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeArchive_InvalidPrxVersion_IsUnsupported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(
            ".prx",
            CreateCompressedPreHeader(0, 0));
        try
        {
            var result = FormatProbe.ProbeArchive(tempFile);

            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Equal("Compressed PRE", result.FormatName);
            Assert.Equal(
                "Invalid compressed PRE header version 0x00000000 " +
                "(expected 0xABCD0002 or 0xABCD0003)",
                result.UnsupportedReason);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeArchive_WadWithoutCompanionHed_IsUnsupported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".wad", [0x00]);
        var hedPath = WadArchive.GetHedPath(tempFile);
        try
        {
            var result = FormatProbe.ProbeArchive(tempFile);

            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Equal("WAD Archive", result.FormatName);
            Assert.Equal("Companion HED file not found", result.UnsupportedReason);
        }
        finally
        {
            File.Delete(hedPath);
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ProbeArchive_WadWithCompanionHed_IsSupported()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(".wad", [0x00]);
        var hedPath = WadArchive.GetHedPath(tempFile);
        try
        {
            File.WriteAllBytes(hedPath, []);

            var result = FormatProbe.ProbeArchive(tempFile);

            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("WAD Archive", result.FormatName);
        }
        finally
        {
            File.Delete(hedPath);
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
    public void ProbeArchive_LocalizedPre_SupportedWithLanguage()
    {
        var data = new byte[8];
        BitConverter.GetBytes(0xABCD0003u).CopyTo(data, 4);
        var tempFile = FormatProbeTestHelper.CreateTempFile(".prd", data);
        try
        {
            var result = FormatProbe.ProbeArchive(tempFile);
            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Contains("Compressed", result.FormatName);
            Assert.Contains("German", result.FormatName);
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

    private static byte[] CreateCompressedPreHeader(uint version, int trailingByteCount)
    {
        var data = new byte[12 + trailingByteCount];
        BitConverter.GetBytes(data.Length).CopyTo(data, 0);
        BitConverter.GetBytes(version).CopyTo(data, 4);
        return data;
    }
}
