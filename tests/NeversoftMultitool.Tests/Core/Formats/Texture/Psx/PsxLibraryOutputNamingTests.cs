using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Texture.Psx;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Psx;

public sealed class PsxLibraryOutputNamingTests
{
    [Theory]
    [InlineData(".psx", "file.psx")]
    [InlineData(".mdl", "file.mdl")]
    [InlineData(".first.psx", ".first")]
    [InlineData("...first.psx", "...first")]
    [InlineData("0012_name.icon.psx", "0012_name.icon")]
    public void GetOutputStem_PureExtensionFallbackPreservesExistingNames(
        string filename,
        string expected)
    {
        Assert.Equal(expected, PsxLibrary.GetOutputStem(filename));
    }

    [Fact]
    public void ExtractTextures_DistinctPureExtensionLabels_DoNotOverwriteEachOther()
    {
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            "NsMtPsxOutputNaming_" + Guid.NewGuid().ToString("N"));
        var firstPath = Path.Combine(outputDirectory, "file.psx_0000002C.png");
        var secondPath = Path.Combine(outputDirectory, "file.mdl_0000002C.png");
        var legacyCollisionPath = Path.Combine(outputDirectory, "_0000002C.png");

        try
        {
            var first = PsxLibrary.ExtractTextures(
                BuildRectangleTexture(0xF800),
                ".psx",
                outputDirectory,
                createSubDirs: false,
                writeDds: false,
                writeMipAtlas: false);
            var second = PsxLibrary.ExtractTextures(
                BuildRectangleTexture(0x001F),
                ".mdl",
                outputDirectory,
                createSubDirs: false,
                writeDds: false,
                writeMipAtlas: false);

            Assert.True(first.Success, first.ErrorMessage);
            Assert.True(second.Success, second.ErrorMessage);
            Assert.True(File.Exists(firstPath));
            Assert.True(File.Exists(secondPath));
            Assert.False(File.Exists(legacyCollisionPath));

            using var firstImage = Image.Load<Rgba32>(firstPath);
            using var secondImage = Image.Load<Rgba32>(secondPath);
            Assert.Equal(new Rgba32(255, 0, 0, 255), firstImage[0, 0]);
            Assert.Equal(new Rgba32(0, 0, 255, 255), secondImage[0, 0]);
            Assert.NotEqual(firstImage[0, 0], secondImage[0, 0]);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    private static byte[] BuildRectangleTexture(ushort color)
    {
        var data = new byte[80];
        data[0] = 0x04;
        data[2] = 0x02;

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), 0xDEAD_BEEF);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(36), 1);

        const int headerOffset = 0x2C;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), headerOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(headerOffset + 4), 65536);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(headerOffset + 16), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(headerOffset + 18), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(headerOffset + 20), 0x901);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(headerOffset + 24), 8);

        for (var offset = headerOffset + 28; offset < data.Length; offset += sizeof(ushort))
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), color);

        return data;
    }
}
