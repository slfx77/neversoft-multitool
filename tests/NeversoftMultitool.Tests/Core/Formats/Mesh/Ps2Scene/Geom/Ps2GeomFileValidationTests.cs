using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Geom;

public sealed class Ps2GeomFileValidationTests
{
    [Fact]
    public void Parse_DataSectionOffsetWithoutCompleteRootPointer_ThrowsInvalidDataException()
    {
        var data = new byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(data, data.Length - 1);

        var exception = Assert.Throws<InvalidDataException>(() => Ps2GeomFile.Parse(data));

        Assert.Equal("Invalid data section offset: 0x13", exception.Message);
    }

    [Fact]
    public void Parse_OverflowingRootNodeOffset_ThrowsInvalidDataException()
    {
        var data = new byte[20];
        BinaryPrimitives.WriteInt32LittleEndian(data, 4);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), int.MaxValue);

        var exception = Assert.Throws<InvalidDataException>(() => Ps2GeomFile.Parse(data));

        Assert.Equal("Invalid root node offset: 0x7FFFFFFF", exception.Message);
    }

    [Fact]
    public void Parse_OverflowingChildNodeOffset_IsIgnored()
    {
        var data = BuildRootNode();
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0x2C), int.MaxValue);

        var scene = Ps2GeomFile.Parse(data);

        Assert.Empty(scene.Leaves);
    }

    [Fact]
    public void Parse_MinimalRootNode_ReturnsEmptyScene()
    {
        var scene = Ps2GeomFile.Parse(BuildRootNode());

        Assert.Empty(scene.Leaves);
    }

    private static byte[] BuildRootNode()
    {
        const int baseOffset = 4;
        var data = new byte[baseOffset + 80];
        BinaryPrimitives.WriteInt32LittleEndian(data, baseOffset);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(baseOffset), 0);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(baseOffset + 0x20), -1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(baseOffset + 0x28), -1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(baseOffset + 0x4C), -1);
        return data;
    }
}
