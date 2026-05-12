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

    [Fact]
    public void ExtractBillboardFromVif_AxisAligned_ReturnsLongAxisDescriptor()
    {
        // Mirrors the 145 axis-Y-aligned Format-B leaves found in z_sm by
        // tools/diagnostics/thaw_billboard_classify.py.
        var data = BuildBillboardStream(
            anchor: new Vector3(10f, 20f, 30f),
            size: new Vector2(4f, 6f),
            pvl: Vector3.Zero,
            axis: new Vector3(0f, 1f, 0f));

        var result = Ps2GeomVifVertexDecoder.ExtractBillboardFromVif(data, 0, data.Length);

        Assert.NotNull(result);
        var descriptor = result.Value.Descriptor;
        Assert.Equal(Ps2BillboardKind.LongAxis, descriptor.Kind);
        Assert.Equal(new Vector3(10f, 20f, 30f), descriptor.Anchor);
        Assert.Equal(new Vector2(4f, 6f), descriptor.Size);
        Assert.Equal(new Vector3(0f, 1f, 0f), descriptor.Axis);
        Assert.Equal(4, result.Value.Vertices.Length);
    }

    [Fact]
    public void ExtractBillboardFromVif_ZeroAxis_ReturnsScreenAlignedDescriptor()
    {
        var data = BuildBillboardStream(
            anchor: new Vector3(1f, 2f, 3f),
            size: new Vector2(8f, 8f),
            pvl: Vector3.Zero,
            axis: Vector3.Zero);

        var result = Ps2GeomVifVertexDecoder.ExtractBillboardFromVif(data, 0, data.Length);

        Assert.NotNull(result);
        Assert.Equal(Ps2BillboardKind.ScreenAligned, result.Value.Descriptor.Kind);
        Assert.Equal(Vector3.Zero, result.Value.Descriptor.Axis);
    }

    [Fact]
    public void ExtractBillboardFromVif_NonZeroPvlAlongAxis_OffsetsQuadCenter()
    {
        // Quad centre = anchor + axis * pvl.z for axis-aligned billboards (verified
        // visually on z_sm lamp-post light flares: positive pvl.z along axis=+Y lifts
        // the glow into the glass housing instead of dropping it below the post).
        var data = BuildBillboardStream(
            anchor: new Vector3(0f, 0f, 0f),
            size: new Vector2(2f, 2f),
            pvl: new Vector3(0f, 0f, 5f),
            axis: new Vector3(0f, 1f, 0f));

        var result = Ps2GeomVifVertexDecoder.ExtractBillboardFromVif(data, 0, data.Length);

        Assert.NotNull(result);
        Assert.Equal(new Vector3(0f, 0f, 5f), result.Value.Descriptor.PivotLocal);
        var vertices = result.Value.Vertices;
        // Quad centre = anchor + axis * pvl.z = (0,0,0) + (0,1,0) * 5 = (0,5,0).
        // Corners are (±1, ±1, 0) offset from the centre.
        var meanY = (vertices[0].Position.Y + vertices[1].Position.Y +
                     vertices[2].Position.Y + vertices[3].Position.Y) / 4f;
        Assert.Equal(5f, meanY, 3);
    }

    [Fact]
    public void ExtractBillboardFromVif_StmodGatedV4_32_ReturnsNull()
    {
        // STMOD(1) gating the V4_32 means this is real Format-A geometry, not Format-B.
        var data = new List<byte>();
        AddVifCode(data, 1, 0, 0x05); // STMOD(1)
        AddVifCode(data, 0x8000, 4, 0x6C); // V4_32 UNPACK num=4 (but STMOD-gated)
        for (var i = 0; i < 16; i++)
            AddFloat(data, 0f);
        AddVifCode(data, 0, 0, 0x14); // MSCAL

        var result = Ps2GeomVifVertexDecoder.ExtractBillboardFromVif(data.ToArray(), 0, data.Count);

        Assert.Null(result);
    }

    [Fact]
    public void ExtractBillboardFromVif_BogusSize_ReturnsNull()
    {
        var data = BuildBillboardStream(
            anchor: new Vector3(0f, 0f, 0f),
            size: new Vector2(-1f, 10f),
            pvl: Vector3.Zero,
            axis: Vector3.Zero);

        var result = Ps2GeomVifVertexDecoder.ExtractBillboardFromVif(data, 0, data.Length);

        Assert.Null(result);
    }

    private static byte[] BuildBillboardStream(Vector3 anchor, Vector2 size, Vector3 pvl, Vector3 axis)
    {
        var data = new List<byte>();
        AddVifCode(data, 0x8000, 4, 0x6C); // V4_32 UNPACK num=4, NOT STMOD-gated
        AddFloat(data, anchor.X); AddFloat(data, anchor.Y); AddFloat(data, anchor.Z); AddFloat(data, 0f);
        AddFloat(data, size.X);   AddFloat(data, size.Y);   AddFloat(data, 0f);        AddFloat(data, 0f);
        AddFloat(data, pvl.X);    AddFloat(data, pvl.Y);    AddFloat(data, pvl.Z);     AddFloat(data, 0f);
        AddFloat(data, axis.X);   AddFloat(data, axis.Y);   AddFloat(data, axis.Z);    AddFloat(data, 0f);
        AddVifCode(data, 0, 0, 0x14); // MSCAL
        return data.ToArray();
    }

    private static void AddFloat(List<byte> data, float value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(buffer, value);
        foreach (var b in buffer)
            data.Add(b);
    }
}
