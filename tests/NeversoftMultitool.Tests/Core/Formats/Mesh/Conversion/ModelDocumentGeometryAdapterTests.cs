using System.Buffers.Binary;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class ModelDocumentGeometryAdapterTests
{
    [Fact]
    public void AddTriangle_NonFinitePosition_IsNotEmitted()
    {
        foreach (var value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var vertices = new List<ModelVertex>();
            var indices = new List<int>();

            ModelDocumentGeometryAdapter.AddTriangle(
                vertices,
                indices,
                Vertex(new Vector3(value, 0f, 0f)),
                Vertex(Vector3.UnitX),
                Vertex(Vector3.UnitY));

            Assert.Empty(vertices);
            Assert.Empty(indices);
        }
    }

    [Fact]
    public void AddSkinnedTriangle_NonFinitePosition_IsNotEmitted()
    {
        var vertices = new List<ModelVertex>();
        var indices = new List<int>();
        var influences = new List<ModelBoneInfluences>();
        var influence = ModelBoneInfluences.Single(0);

        ModelDocumentGeometryAdapter.AddSkinnedTriangle(
            vertices,
            indices,
            influences,
            Vertex(Vector3.Zero), influence,
            Vertex(new Vector3(0f, float.NaN, 0f)), influence,
            Vertex(Vector3.UnitY), influence);

        Assert.Empty(vertices);
        Assert.Empty(indices);
        Assert.Empty(influences);
    }

    [Fact]
    public void AddTriangle_FiniteNondegeneratePositions_IsEmitted()
    {
        var vertices = new List<ModelVertex>();
        var indices = new List<int>();

        ModelDocumentGeometryAdapter.AddTriangle(
            vertices,
            indices,
            Vertex(Vector3.Zero),
            Vertex(Vector3.UnitX),
            Vertex(Vector3.UnitY));

        Assert.Equal(3, vertices.Count);
        Assert.Equal([0, 1, 2], indices);
    }

    [Fact]
    public void TryExtractPngDimensions_NonPngDimensionWords_ReturnsNull()
    {
        var bytes = new byte[24];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), 64);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), 32);

        Assert.Null(ModelDocumentGeometryAdapter.TryExtractPngDimensions(bytes));
    }

    [Theory]
    [InlineData(24)]
    [InlineData(32)]
    public void TryExtractPngDimensions_TruncatedIhdr_ReturnsNull(int length)
    {
        var bytes = FramedPngHeader()[..length];

        Assert.Null(ModelDocumentGeometryAdapter.TryExtractPngDimensions(bytes));
    }

    [Fact]
    public void TryExtractPngDimensions_InvalidFraming_ReturnsNull()
    {
        var wrongSignature = FramedPngHeader();
        wrongSignature[0] = 0;
        var wrongLength = FramedPngHeader();
        BinaryPrimitives.WriteUInt32BigEndian(wrongLength.AsSpan(8, 4), 12);
        var wrongChunkType = FramedPngHeader();
        wrongChunkType[12] = (byte)'J';

        Assert.Null(ModelDocumentGeometryAdapter.TryExtractPngDimensions(wrongSignature));
        Assert.Null(ModelDocumentGeometryAdapter.TryExtractPngDimensions(wrongLength));
        Assert.Null(ModelDocumentGeometryAdapter.TryExtractPngDimensions(wrongChunkType));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public void TryExtractPngDimensions_FramedIhdr_ReturnsDimensions(int trailingByteCount)
    {
        var bytes = FramedPngHeader(trailingByteCount);

        var dimensions = ModelDocumentGeometryAdapter.TryExtractPngDimensions(bytes);
        Assert.True(dimensions.HasValue);
        Assert.Equal(64, dimensions.Value.Width);
        Assert.Equal(32, dimensions.Value.Height);
    }

    private static ModelVertex Vertex(Vector3 position) =>
        new(position, Vector3.UnitZ, Vector4.One, Vector2.Zero);

    private static byte[] FramedPngHeader(int trailingByteCount = 0)
    {
        var bytes = new byte[33 + trailingByteCount];
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), 64);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), 32);
        bytes[24] = 8;
        bytes[25] = 6;
        return bytes;
    }
}
