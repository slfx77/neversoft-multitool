using System.Buffers.Binary;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Trg;

namespace NeversoftMultitool.Tests.Core.Formats.Trg;

public sealed class TrgNodeStringBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParseRestart_NameTerminatorOutsideNode_FallsBackWithoutBorrowing(bool bigEndian)
    {
        var bytes = BuildRestartNode(bigEndian, (byte)'A', 0);
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new EndianBinaryReader(stream, bigEndian);

        var node = TrgNode.Parse(reader, index: 0, offset: 0, nodeSize: 23, isSpiderMan: true);

        Assert.Null(node.Name);
        Assert.Equal(Convert.ToHexString(bytes.AsSpan(0, 23)), node.RawHex);
        Assert.Equal(23, stream.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParseRestart_NameAlignmentOutsideNode_FallsBackWithoutSeeking(bool bigEndian)
    {
        var bytes = BuildRestartNode(bigEndian, 0, 0);
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new EndianBinaryReader(stream, bigEndian);

        var node = TrgNode.Parse(reader, index: 0, offset: 0, nodeSize: 23, isSpiderMan: true);

        Assert.Null(node.Name);
        Assert.Equal(Convert.ToHexString(bytes.AsSpan(0, 23)), node.RawHex);
        Assert.Equal(23, stream.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParseRestart_OwnedNameTerminator_ParsesName(bool bigEndian)
    {
        var bytes = BuildRestartNode(bigEndian, (byte)'A', 0);
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new EndianBinaryReader(stream, bigEndian);

        var node = TrgNode.Parse(reader, index: 0, offset: 0, nodeSize: 24, isSpiderMan: true);

        Assert.Equal("A", node.Name);
        Assert.Null(node.RawHex);
        Assert.Equal(24, stream.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParseRestart_OwnedEmptyNameAlignment_ParsesName(bool bigEndian)
    {
        var bytes = BuildRestartNode(bigEndian, 0, 0);
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new EndianBinaryReader(stream, bigEndian);

        var node = TrgNode.Parse(reader, index: 0, offset: 0, nodeSize: 24, isSpiderMan: true);

        Assert.Equal(string.Empty, node.Name);
        Assert.Null(node.RawHex);
        Assert.Equal(24, stream.Position);
    }

    private static byte[] BuildRestartNode(bool bigEndian, byte firstNameByte, byte nextByte)
    {
        var bytes = new byte[24];
        if (bigEndian)
            BinaryPrimitives.WriteUInt16BigEndian(bytes, TrgNodeMetadata.TypeRestart);
        else
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, TrgNodeMetadata.TypeRestart);
        bytes[22] = firstNameByte;
        bytes[23] = nextByte;
        return bytes;
    }
}
