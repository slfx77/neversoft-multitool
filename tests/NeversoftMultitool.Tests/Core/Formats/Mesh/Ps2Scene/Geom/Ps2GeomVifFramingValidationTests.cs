using System.Buffers.Binary;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Geom;

public sealed class Ps2GeomVifFramingValidationTests
{
    [Fact]
    public void ExtractVerticesFromVif_UnpackPayloadCrossingEnd_ReturnsEmpty()
    {
        var data = BuildSinglePositionStream();

        var vertices = Ps2GeomVifVertexDecoder.ExtractVerticesFromVif(
            data,
            0,
            8,
            Vector3.Zero);

        Assert.Empty(vertices);
    }

    [Fact]
    public void ExtractVerticesFromVif_UnpackPayloadEndingAtEnd_DecodesVertex()
    {
        var data = BuildSinglePositionStream();

        var vertices = Ps2GeomVifVertexDecoder.ExtractVerticesFromVif(
            data,
            0,
            data.Length,
            Vector3.Zero);

        var vertex = Assert.Single(vertices);
        Assert.Equal(new Vector3(1f, 2f, 3f), vertex.Position);
    }

    private static byte[] BuildSinglePositionStream()
    {
        var data = new byte[16];

        // STMOD(1), followed by one V4_16 position UNPACK.
        data[0] = 1;
        data[3] = 0x05;
        data[6] = 1;
        data[7] = 0x6D;

        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(8), 16);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(10), 32);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(12), 48);
        return data;
    }
}
