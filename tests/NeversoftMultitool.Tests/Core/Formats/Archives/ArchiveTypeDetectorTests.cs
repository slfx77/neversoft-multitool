using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Tests.Core.Formats.Archives;

public sealed class ArchiveTypeDetectorTests
{
    [Theory]
    [InlineData("nested.pak.zip", ".zip", true)]
    [InlineData("bundle.pre.pkr", ".pkr", true)]
    [InlineData("level.pak.ps2", ".pak", true)]
    [InlineData("hair.tex.zip.wpc", ".zip", true)]
    [InlineData("nested.pak.zip.wpc", ".zip", true)]
    [InlineData("level.pak.ps2.bak", ".bak", false)]
    public void GetArchiveExtension_PrefersOuterArchiveBeforePlatformSuffix(
        string fileName,
        string expectedExtension,
        bool expectedArchive)
    {
        Assert.Equal(expectedExtension, ArchiveTypeDetector.GetArchiveExtension(fileName));
        Assert.Equal(expectedArchive, ArchiveTypeDetector.IsArchiveFile(fileName));
    }

    [Fact]
    public void DetectAssetType_PrxMissingTruncatedOrInvalidHeader_ReturnsNull()
    {
        var path = CreateTempPath(".PrX");
        try
        {
            Assert.Null(ArchiveTypeDetector.DetectAssetType(path));

            for (var length = 0; length < 12; length++)
            {
                File.WriteAllBytes(path, new byte[length]);
                Assert.Null(ArchiveTypeDetector.DetectAssetType(path));
            }

            File.WriteAllBytes(path, CreateCompressedPreHeader(0xABCD0005));
            Assert.Null(ArchiveTypeDetector.DetectAssetType(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DetectNestedAssetType_PrxTruncatedOrInvalidHeader_ReturnsNull()
    {
        for (var length = 0; length < 12; length++)
        {
            Assert.Null(ArchiveTypeDetector.DetectNestedAssetType(
                "nested.prx",
                new byte[length]));
        }

        Assert.Null(ArchiveTypeDetector.DetectNestedAssetType(
            "nested.prx",
            CreateCompressedPreHeader(0xABCD0005)));
    }

    /// <summary>
    ///     PSP builds ship Sony's own firmware .prx (PlayStation Relocatable
    ///     eXecutable) modules under Modules/, which share only the extension
    ///     with Neversoft's Xbox compressed-PRE .prx. They must classify as
    ///     "(raw)" so the recursive unpacker skips them instead of erroring
    ///     (13 such modules per PSP build).
    /// </summary>
    [Fact]
    public void Classify_SonyFirmwarePrx_IsRawNotPre()
    {
        var path = CreateTempPath(".prx");
        try
        {
            // A real module starts "~PSP"; any non-0xABCDxxxx dword behaves the same.
            File.WriteAllBytes(path, [0x7E, 0x50, 0x53, 0x50, 0x06, 0x10, 0x01, 0x00, 0, 0, 0, 0, 0, 0, 0, 0]);
            Assert.Equal("PRE3 (raw)", ArchiveTypeDetector.Classify(path));
            Assert.Null(ArchiveTypeDetector.DetectAssetType(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0xABCD0002u, ".prx", 0)]
    [InlineData(0xABCD0003u, ".PRX", 0)]
    [InlineData(0xABCD0003u, ".PrX", 5)]
    [InlineData(0xABCD0004u, ".prx", 0)]
    public void DetectAssetTypes_ValidPrxHeader_ReturnCompressedPre(
        uint version,
        string extension,
        int trailingByteCount)
    {
        var header = CreateCompressedPreHeader(version);
        var data = new byte[header.Length + trailingByteCount];
        header.CopyTo(data, 0);
        var path = CreateTempPath(extension);
        try
        {
            File.WriteAllBytes(path, data);

            Assert.Equal(
                ArchiveAssetType.CompressedPre,
                ArchiveTypeDetector.DetectAssetType(path));
            Assert.Equal(
                ArchiveAssetType.CompressedPre,
                ArchiveTypeDetector.DetectNestedAssetType($"nested{extension}", data));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] CreateCompressedPreHeader(uint version)
    {
        var header = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), version);
        return header;
    }

    private static string CreateTempPath(string extension)
    {
        return Path.Combine(Path.GetTempPath(), $"nmt-archive-type-{Guid.NewGuid():N}{extension}");
    }
}
