using NeversoftMultitool.Core.Formats.Texture.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Psx;

public class PsxLibraryOrdinalLookupTests
{
    private const uint DuplicateHash = 0x12345678;

    [Fact]
    public void ExtractTextureAt_DuplicateHashes_SelectsPhysicalTextureRecord()
    {
        var data = BuildTwoTextureLibrary();

        var entries = PsxLibrary.EnumerateTextures(data);
        var first = PsxLibrary.ExtractTextureAt(data, 0, "duplicate.psx");
        var second = PsxLibrary.ExtractTextureAt(data, 1, "duplicate.psx");

        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal(DuplicateHash, entry.NameHash));
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal((2, 1), (first.Value.Width, first.Value.Height));
        Assert.Equal((2, 1), (second.Value.Width, second.Value.Height));
        Assert.Equal(
            new byte[] { 255, 0, 0, 255, 255, 0, 0, 255 },
            first.Value.Rgba);
        Assert.Equal(
            new byte[] { 0, 0, 255, 255, 0, 0, 255, 255 },
            second.Value.Rgba);
    }

    private static byte[] BuildTwoTextureLibrary()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(new byte[] { 0x04, 0x00, 0x02, 0x00 });
        writer.Write(16u); // tagged-chunk list
        writer.Write(0u); // object count
        writer.Write(0u); // mesh count
        writer.Write(uint.MaxValue); // end of tagged chunks

        writer.Write(2u); // texture-name count
        writer.Write(DuplicateHash);
        writer.Write(DuplicateHash);

        writer.Write(2u); // 4-bit palette count
        WritePalette(writer, 0x11111111, 0x001F); // red
        WritePalette(writer, 0x22222222, 0x7C00); // blue
        writer.Write(0u); // 8-bit palette count

        writer.Write(2u); // physical texture count
        writer.Write(124u); // first texture record
        writer.Write(148u); // second texture record

        // Physical record order and name-table index are separate domains in
        // the runtime format. Swap the logical indices so this fixture catches
        // accidental index-based lookup as well as duplicate-hash lookup.
        WriteTexture(writer, 0x11111111, 1);
        WriteTexture(writer, 0x22222222, 0);
        return stream.ToArray();
    }

    private static void WritePalette(BinaryWriter writer, uint textureId, ushort color)
    {
        writer.Write(textureId);
        for (var i = 0; i < 16; i++)
            writer.Write(color);
    }

    private static void WriteTexture(BinaryWriter writer, uint textureId, uint nameIndex)
    {
        writer.Write(0u); // flags
        writer.Write(16u); // palette size
        writer.Write(textureId);
        writer.Write(nameIndex);
        writer.Write((ushort)2);
        writer.Write((ushort)1);
        writer.Write(0u); // two indexed pixels plus row padding
    }
}
