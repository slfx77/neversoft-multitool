using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Ps2Scene;

public sealed class Ps2VramAllocatorTests
{
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
}
