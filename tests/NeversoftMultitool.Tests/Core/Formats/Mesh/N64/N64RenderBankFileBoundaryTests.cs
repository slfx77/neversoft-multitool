using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Mesh.N64;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.N64;

public sealed class N64RenderBankFileBoundaryTests
{
    [Fact]
    public void Parse_NonZeroVertexPoolReservedWord_SkipsMalformedNode()
    {
        var meshes = N64RenderBankFile.Parse(BuildSingleVertexBank(poolReserved: 1));

        Assert.Empty(meshes);
    }

    [Fact]
    public void Parse_ZeroVertexPoolReservedWord_PreservesValidNode()
    {
        var mesh = Assert.Single(N64RenderBankFile.Parse(BuildSingleVertexBank(poolReserved: 0)));

        Assert.Single(mesh.Vertices);
        Assert.Empty(mesh.Triangles);
    }

    [Fact]
    public void Parse_OverflowingNestedTableOffsets_ReturnsEmpty()
    {
        var data = new byte[64];
        WriteTable(data, 0, [12u, 64u]);
        WriteTable(data, 12, [20u, 20u, 20u, int.MaxValue]);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(32), 1);

        var meshes = N64RenderBankFile.Parse(data);

        Assert.Empty(meshes);
    }

    private static byte[] BuildSingleVertexBank(uint poolReserved)
    {
        // Root: one child at 12..56. Node: empty bounds/geometry plus a
        // 24-byte pool at 32..56 containing its 8-byte header and one vertex.
        var data = Convert.FromHexString(
            "000000010000000C00000038000000030000001400000014000000140000002C" +
            "000000010000000000000000000000000000000000000000");
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(36), poolReserved);
        return data;
    }

    private static void WriteTable(byte[] data, int offset, uint[] childOffsets)
    {
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), (uint)childOffsets.Length - 1);
        for (var i = 0; i < childOffsets.Length; i++)
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset + 4 + i * 4), childOffsets[i]);
    }
}
