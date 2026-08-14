using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Ps2Scene;

public sealed class Ps2VramAllocatorTests
{
    [Fact]
    public void BuildMapping_ShiftWrappedTextureWidth_ReturnsEmpty()
    {
        var data = CreateVersion3Texture(tw: 32, th: 0, psm: 0, cpsm: 0, payloadBytes: 4);

        var mapping = Ps2VramAllocator.BuildMapping(data);

        Assert.Empty(mapping);
    }

    [Fact]
    public void BuildMapping_MaximumTextureWidth_RemainsValid()
    {
        const uint checksum = 0x89ABCDEF;
        // PSMT4 at 2048x1: 32-byte PSMCT16 CLUT plus 1024 bytes of pixels.
        var data = CreateVersion3Texture(
            tw: 11, th: 0, psm: 0x14, cpsm: 0x02, payloadBytes: 32 + 1024);

        var entry = Assert.Single(Ps2VramAllocator.BuildMapping(data));

        Assert.Equal(checksum, entry.Value);
    }

    [Fact]
    public void BuildMapping_TruncatedGroupHeader_ReturnsEmpty()
    {
        var data = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 3);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 0x12345678);

        var mapping = Ps2VramAllocator.BuildMapping(data);

        Assert.Empty(mapping);
    }

    [Fact]
    public void BuildMapping_TruncatedVersion5TextureHeader_ReturnsEmpty()
    {
        // Header (12) + complete v5 group header (16) + only the 24-byte
        // v3/v4 texture header. A v5 entry needs four more flag bytes.
        var data = new byte[12 + 16 + 24];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 5);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 0x12345678);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), 1);

        var mapping = Ps2VramAllocator.BuildMapping(data);

        Assert.Empty(mapping);
    }

    private static byte[] CreateVersion3Texture(
        uint tw, uint th, uint psm, uint cpsm, int payloadBytes)
    {
        var data = new byte[48 + payloadBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 3); // version
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 1); // group count
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 1); // total texture count
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 0x12345678); // group checksum
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), 1); // texture count
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), 0x89ABCDEF); // texture checksum
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), tw);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32), th);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(36), psm);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), cpsm);
        // MXL at offset 44 remains zero.
        return data;
    }
}
