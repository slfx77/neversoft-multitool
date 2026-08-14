using NeversoftMultitool.Core.Formats.Texture.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Psx;

public sealed class PsxLibraryLookupPayloadValidationTests
{
    private const uint NameHash = 0x12345678;
    private const uint SupportedPixelFormat = 0x901;
    private const uint UnsupportedPixelFormat = 0x7F00;

    [Theory]
    [InlineData(16u, 3, 3, 8)]
    [InlineData(256u, 3, 3, 12)]
    [InlineData(65536u, 2, 2, 8)]
    public void EnumerateTextures_TruncatedRecognizedPayload_ThrowsInvalidDataException(
        uint paletteSize,
        int width,
        int height,
        int expectedPayloadLength)
    {
        var data = BuildSingleTextureLibrary(
            paletteSize,
            payloadLength: expectedPayloadLength - 1,
            width,
            height,
            declaredSize: (uint)expectedPayloadLength);

        var exception = Assert.Throws<InvalidDataException>(
            () => PsxLibrary.EnumerateTextures(data));

        Assert.Equal(
            $"PSX texture payload is truncated: expected {expectedPayloadLength} bytes, " +
            $"but only {expectedPayloadLength - 1} remain",
            exception.Message);
    }

    [Theory]
    [InlineData(16u, 3, 3, 8)]
    [InlineData(16u, 3, 3, 9)]
    [InlineData(256u, 3, 3, 12)]
    [InlineData(256u, 3, 3, 13)]
    [InlineData(65536u, 2, 2, 8)]
    [InlineData(65536u, 2, 2, 9)]
    public void EnumerateTextures_CompleteOrTrailingRecognizedPayload_RemainsListed(
        uint paletteSize,
        int width,
        int height,
        int payloadLength)
    {
        var expectedPayloadLength = paletteSize == 256 ? 12 : 8;
        var data = BuildSingleTextureLibrary(
            paletteSize,
            payloadLength,
            width,
            height,
            declaredSize: (uint)expectedPayloadLength);

        var texture = Assert.Single(PsxLibrary.EnumerateTextures(data));

        Assert.Equal(paletteSize, texture.Header.PalSize);
        Assert.Equal(NameHash, texture.NameHash);
    }

    [Fact]
    public void EnumerateTextures_UnknownPaletteSize_RemainsListed()
    {
        const uint unknownPaletteSize = 12345;
        var data = BuildSingleTextureLibrary(unknownPaletteSize, payloadLength: 0);

        var texture = Assert.Single(PsxLibrary.EnumerateTextures(data));

        Assert.Equal(unknownPaletteSize, texture.Header.PalSize);
        Assert.Equal($"Unknown ({unknownPaletteSize})", PsxLibrary.DescribePaletteType(texture.Header));
    }

    [Fact]
    public void EnumerateTextures_UnsupportedPixelFormat_RemainsListed()
    {
        var data = BuildSingleTextureLibrary(
            65536,
            payloadLength: 8,
            width: 2,
            height: 2,
            pixelFormat: UnsupportedPixelFormat,
            declaredSize: 8);

        var texture = Assert.Single(PsxLibrary.EnumerateTextures(data));

        Assert.Equal(UnsupportedPixelFormat, texture.Header.PixelFormat);
    }

    [Fact]
    public void EnumerateTextures_ZeroTextures_ReturnsEmpty()
    {
        var data = BuildEmptyLibrary();

        Assert.Empty(PsxLibrary.EnumerateTextures(data));
    }

    private static byte[] BuildSingleTextureLibrary(
        uint paletteSize,
        int payloadLength,
        int width = 1,
        int height = 1,
        uint pixelFormat = SupportedPixelFormat,
        uint declaredSize = 4)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteLibraryPrefix(writer, textureNameCount: 1);
        writer.Write(NameHash);
        writer.Write(0u); // 4-bit palette count
        writer.Write(0u); // 8-bit palette count
        writer.Write(1u); // physical texture count
        writer.Write(44u); // texture record offset

        writer.Write(0u); // flags
        writer.Write(paletteSize);
        writer.Write(0x01020304u); // texture/palette ID
        writer.Write(0u); // texture-name index

        writer.Write((ushort)width);
        writer.Write((ushort)height);

        if (paletteSize == 65536)
        {
            writer.Write(pixelFormat);
            writer.Write(declaredSize);
        }

        writer.Write(new byte[payloadLength]);
        return stream.ToArray();
    }

    private static byte[] BuildEmptyLibrary()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        WriteLibraryPrefix(writer, textureNameCount: 0);
        writer.Write(0u); // 4-bit palette count
        writer.Write(0u); // 8-bit palette count
        writer.Write(0u); // physical texture count
        return stream.ToArray();
    }

    private static void WriteLibraryPrefix(BinaryWriter writer, uint textureNameCount)
    {
        writer.Write(new byte[] { 0x04, 0x00, 0x02, 0x00 });
        writer.Write(16u); // tagged-chunk list offset
        writer.Write(0u); // object count
        writer.Write(0u); // mesh count
        writer.Write(uint.MaxValue); // end of tagged chunks
        writer.Write(textureNameCount);
    }
}
