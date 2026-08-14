using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.RenderWare;

public sealed class RwDffFileValidationTests
{
    private const uint ActualClumpPayloadSize = 16;

    [Fact]
    public void Parse_ClumpDeclaredPastEnd_ThrowsInvalidDataException()
    {
        var data = CreateEmptyClump(ActualClumpPayloadSize + 4);

        Assert.Throws<InvalidDataException>(() => RwDffFile.Parse(data));
    }

    [Fact]
    public void Parse_ExactEmptyClump_ReturnsEmptyCollections()
    {
        var clump = RwDffFile.Parse(CreateEmptyClump(ActualClumpPayloadSize));

        Assert.Empty(clump.Frames);
        Assert.Empty(clump.Geometries);
        Assert.Empty(clump.Atomics);
    }

    [Fact]
    public void Parse_ClumpStructSizeWhoseIntCastMovesOffsetBackward_ThrowsInvalidDataException()
    {
        var data = CreateEmptyClump(ActualClumpPayloadSize);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), uint.MaxValue);

        Assert.Throws<InvalidDataException>(() => RwDffFile.Parse(data));
    }

    private static byte[] CreateEmptyClump(uint declaredPayloadSize)
    {
        var data = new byte[12 + ActualClumpPayloadSize];
        WriteChunkHeader(data, 0, RwChunkReader.RW_CLUMP, declaredPayloadSize);
        WriteChunkHeader(data, 12, RwChunkReader.RW_STRUCT, 4);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24), 0); // numAtomics
        return data;
    }

    private static void WriteChunkHeader(byte[] data, int offset, uint type, uint size)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), type);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 4), size);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 8), 0); // version
    }
}
