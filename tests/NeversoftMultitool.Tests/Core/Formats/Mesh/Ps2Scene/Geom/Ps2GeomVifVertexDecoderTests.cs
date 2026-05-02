using System.Buffers.Binary;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Geom;

public sealed class Ps2GeomVifVertexDecoderTests
{
    [Fact]
    public void ExtractVerticesFromVif_DecodesV3_16UvAtVuAddressSeven()
    {
        var data = new List<byte>();
        AddVifCode(data, 0, 0, 0x02);      // OFFSET(0)
        AddVifCode(data, 0x0101, 0, 0x01); // STCYCL(1,1)
        AddVifCode(data, 0x8007, 4, 0x69); // UNPACK V3_16 to address 0x007: S,T,Q
        AddUv(data, 0, 0, 1);
        AddUv(data, 4096, 0, 1);
        AddUv(data, 0, 8192, 1);
        AddUv(data, 4096, 8192, 1);
        AddVifCode(data, 1, 0, 0x05);      // STMOD(1)
        AddVifCode(data, 0x8009, 4, 0x6D); // UNPACK V4_16 positions
        AddPosition(data, 0, 0, 0, 0x8000);
        AddPosition(data, 16, 0, 0, 0x8000);
        AddPosition(data, 0, 16, 0, 0);
        AddPosition(data, 16, 16, 0, 0);
        AddVifCode(data, 0, 0, 0x05);      // STMOD(0)
        AddVifCode(data, 8, 0, 0x14);      // MSCAL

        var vertices = Ps2GeomVifVertexDecoder.ExtractVerticesFromVif(
            data.ToArray(), 0, data.Count, Vector3.Zero);

        Assert.Equal(4, vertices.Length);
        Assert.All(vertices, vertex => Assert.True(vertex.HasUV));
        Assert.Equal(0f, vertices[0].U);
        Assert.Equal(0f, vertices[0].V);
        Assert.Equal(1f, vertices[1].U);
        Assert.Equal(0f, vertices[1].V);
        Assert.Equal(0f, vertices[2].U);
        Assert.Equal(2f, vertices[2].V);
        Assert.Equal(1f, vertices[3].U);
        Assert.Equal(2f, vertices[3].V);
    }

    private static void AddVifCode(List<byte> data, ushort imm, byte num, byte cmd)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, imm);
        buffer[2] = num;
        buffer[3] = cmd;
        foreach (var b in buffer)
            data.Add(b);
    }

    private static void AddUv(List<byte> data, short s, short t, short q)
    {
        Span<byte> buffer = stackalloc byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(buffer, s);
        BinaryPrimitives.WriteInt16LittleEndian(buffer[2..], t);
        BinaryPrimitives.WriteInt16LittleEndian(buffer[4..], q);
        foreach (var b in buffer)
            data.Add(b);
    }

    private static void AddPosition(List<byte> data, short x, short y, short z, ushort w)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt16LittleEndian(buffer, x);
        BinaryPrimitives.WriteInt16LittleEndian(buffer[2..], y);
        BinaryPrimitives.WriteInt16LittleEndian(buffer[4..], z);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[6..], w);
        foreach (var b in buffer)
            data.Add(b);
    }
}
