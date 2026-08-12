using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Mesh.N64;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.N64;

public sealed class N64RenderBankFileBoundaryTests
{
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

    private static void WriteTable(byte[] data, int offset, uint[] childOffsets)
    {
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), (uint)childOffsets.Length - 1);
        for (var i = 0; i < childOffsets.Length; i++)
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset + 4 + i * 4), childOffsets[i]);
    }
}
