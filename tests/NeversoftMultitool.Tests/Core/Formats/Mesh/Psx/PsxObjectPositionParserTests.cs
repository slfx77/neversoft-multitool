using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Psx;

public sealed class PsxObjectPositionParserTests
{
    [Fact]
    public void ParsePositions_TruncatedRecognizedHeader_ReturnsNull()
    {
        WithTempPsx([0x03, 0x00, 0x02, 0x00], path =>
            Assert.Null(PsxObjectPositionParser.ParsePositions(path)));
    }

    [Fact]
    public void ParsePositions_TruncatedDeclaredObjectTable_ReturnsNull()
    {
        var data = CreateHeader(objectCount: 1);

        WithTempPsx(data, path =>
            Assert.Null(PsxObjectPositionParser.ParsePositions(path)));
    }

    [Fact]
    public void ParsePositions_ExactOneObjectTable_DecodesPositionAndMeshIndex()
    {
        var data = new byte[12 + 36];
        CreateHeader(objectCount: 1).CopyTo(data, 0);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12 + 4), 4096);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12 + 8), -8192);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12 + 12), 2048);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12 + 22), 0x1234);

        WithTempPsx(data, path =>
        {
            var position = Assert.Single(PsxObjectPositionParser.ParsePositions(path)!);
            Assert.Equal(1f, position.X);
            Assert.Equal(-2f, position.Y);
            Assert.Equal(0.5f, position.Z);
            Assert.Equal((ushort)0x1234, position.MeshIndex);
        });
    }

    private static byte[] CreateHeader(uint objectCount)
    {
        var data = new byte[12];
        data[0] = 0x03;
        data[2] = 0x02;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), objectCount);
        return data;
    }

    private static void WithTempPsx(byte[] data, Action<string> assertion)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nmt-psx-position-{Guid.NewGuid():N}.psx");
        try
        {
            File.WriteAllBytes(path, data);
            assertion(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
