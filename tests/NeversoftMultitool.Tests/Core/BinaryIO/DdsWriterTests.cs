using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Texture;
using NeversoftMultitool.Core.Formats.Texture.Pvr;

namespace NeversoftMultitool.Tests.Core.BinaryIO;

public sealed class DdsWriterTests
{
    private const uint DdsdMipmapCount = 0x20000;
    private const uint DdsCapsTexture = 0x1000;
    private const uint DdsCapsComplex = 0x8;
    private const uint DdsCapsMipmap = 0x400000;

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void WriteDds_MismatchedSingleSurface_RejectsBeforeCreatingOutput(int pixelCount)
    {
        var (outputRoot, outputPath) = CreateAbsentOutputPath();
        try
        {
            var error = Assert.Throws<ArgumentException>(() =>
                DdsWriter.WriteDds(outputPath, 2, 2, ColorFormat.Rgb565, new ushort[pixelCount]));

            Assert.Equal("pixelData", error.ParamName);
            Assert.False(Directory.Exists(outputRoot));
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            if (Directory.Exists(outputRoot))
                Directory.Delete(outputRoot, true);
        }
    }

    [Theory]
    [InlineData(0, 2, "width")]
    [InlineData(-1, 2, "width")]
    [InlineData(2, 0, "height")]
    [InlineData(2, -1, "height")]
    public void WriteDds_NonPositiveDimension_RejectsBeforeCreatingOutput(
        int width,
        int height,
        string parameterName)
    {
        var (outputRoot, outputPath) = CreateAbsentOutputPath();
        try
        {
            var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
                DdsWriter.WriteDds(outputPath, width, height, ColorFormat.Rgb565, []));

            Assert.Equal(parameterName, error.ParamName);
            Assert.False(Directory.Exists(outputRoot));
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            if (Directory.Exists(outputRoot))
                Directory.Delete(outputRoot, true);
        }
    }

    [Fact]
    public void WriteDds_PayloadBeyondRuntimeByteArrayLimit_RejectsBeforeCreatingOutput()
    {
        var (outputRoot, outputPath) = CreateAbsentOutputPath();
        var width = Array.MaxLength / sizeof(ushort) + 1;
        try
        {
            var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
                DdsWriter.WriteDds(outputPath, width, 1, ColorFormat.Rgb565, []));

            Assert.Equal("width", error.ParamName);
            Assert.Contains("16-bit DDS", error.Message, StringComparison.Ordinal);
            Assert.Contains("byte-array limit", error.Message, StringComparison.Ordinal);
            Assert.False(Directory.Exists(outputRoot));
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            if (Directory.Exists(outputRoot))
                Directory.Delete(outputRoot, true);
        }
    }

    [Fact]
    public void WriteDds_ExactSingleSurface_WritesDeclaredHeaderAndPayload()
    {
        var path = GetTempPath();
        try
        {
            DdsWriter.WriteDds(
                path,
                2,
                2,
                ColorFormat.Rgb565,
                [0x1111, 0x2222, 0x3333, 0x4444]);

            var bytes = File.ReadAllBytes(path);
            Assert.Equal(136, bytes.Length);
            Assert.Equal(2u, BitConverter.ToUInt32(bytes, 12));
            Assert.Equal(2u, BitConverter.ToUInt32(bytes, 16));
            Assert.Equal(4u, BitConverter.ToUInt32(bytes, 20));
            Assert.Equal(
                new byte[] { 0x11, 0x11, 0x22, 0x22, 0x33, 0x33, 0x44, 0x44 },
                bytes[128..]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WriteDds_SingleLevelChain_WritesOrdinaryTextureHeader()
    {
        var path = GetTempPath();
        try
        {
            DdsWriter.WriteDds(path, ColorFormat.Rgb565, new PvrMipChain
            {
                Width = 2,
                Height = 2,
                Levels = [new ushort[4]]
            });

            var (flags, mipMapCount, caps) = ReadMipHeader(path);
            Assert.Equal(0u, flags & DdsdMipmapCount);
            Assert.Equal(0u, mipMapCount);
            Assert.Equal(DdsCapsTexture, caps);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WriteDds_MultipleLevels_WritesMipmappedTextureHeader()
    {
        var path = GetTempPath();
        try
        {
            DdsWriter.WriteDds(path, ColorFormat.Rgb565, new PvrMipChain
            {
                Width = 2,
                Height = 2,
                Levels = [new ushort[4], new ushort[1]]
            });

            var (flags, mipMapCount, caps) = ReadMipHeader(path);
            Assert.Equal(DdsdMipmapCount, flags & DdsdMipmapCount);
            Assert.Equal(2u, mipMapCount);
            Assert.Equal(DdsCapsTexture | DdsCapsComplex | DdsCapsMipmap, caps);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string GetTempPath()
    {
        return Path.Combine(Path.GetTempPath(), $"nmt-dds-writer-{Guid.NewGuid():N}.dds");
    }

    private static (string OutputRoot, string OutputPath) CreateAbsentOutputPath()
    {
        var outputRoot = Path.Combine(
            Path.GetTempPath(),
            nameof(DdsWriterTests),
            Guid.NewGuid().ToString("N"));
        return (outputRoot, Path.Combine(outputRoot, "nested", "output.dds"));
    }

    private static (uint Flags, uint MipMapCount, uint Caps) ReadMipHeader(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        stream.Position = 8;
        var flags = reader.ReadUInt32();
        stream.Position = 28;
        var mipMapCount = reader.ReadUInt32();
        stream.Position = 108;
        var caps = reader.ReadUInt32();
        return (flags, mipMapCount, caps);
    }
}
